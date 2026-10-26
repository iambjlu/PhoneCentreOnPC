using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Adb;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

public sealed record AppEntry(string Package, string Label, bool IsSystem);

/// <summary>一台裝置上的 App 清單、圖示，以及執行中／最近使用的 App（對應 AppCatalog.swift）。只在 UI 執行緒存取。</summary>
public sealed class AppCatalog
{
    public Device Device { get; set; }
    public IReadOnlyList<AppEntry> Apps { get; private set; } = [];
    /// <summary>已開啟（有程序在跑）的 App，依最近使用排序</summary>
    public IReadOnlyList<string> Running { get; private set; } = [];
    /// <summary>目前在手機畫面最上層的 App（桌面、系統介面不算）</summary>
    public string? Foreground { get; private set; }
    public IReadOnlyList<string> Recent { get; private set; } = [];
    public bool Loading { get; private set; }

    public event Action? Changed;

    private readonly Dictionary<string, BitmapImage> _icons = new();
    private readonly HashSet<string> _iconRequests = [];
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private string? _homePackage;
    private HashSet<string> _installed = [];

    public AppCatalog(Device device) => Device = device;

    public string Label(string pkg) =>
        Apps.FirstOrDefault(a => a.Package == pkg)?.Label ?? pkg.Split('.').Last();

    private Task<string> Helper(IEnumerable<string> args, double timeout = 60) => HelperTool.Shared.RunAsync(Device, args, timeout);

    // MARK: App 清單

    public async Task ReloadAsync()
    {
        Loading = true;
        try
        {
            var outp = await Helper(["list"]);
            var list = new List<AppEntry>();
            foreach (var line in outp.Split('\n'))
            {
                var p = line.TrimEnd('\r').Split('\t');
                if (p.Length >= 3) list.Add(new AppEntry(p[0], p[1], p[2] == "1"));
            }
            if (list.Count == 0)
            {
                // 輔助程式失敗時的備援：只有套件名稱
                var o2 = await Device.Client.ShellAsync(
                    "cmd package query-activities --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER");
                foreach (var l in o2.Split('\n').Select(x => x.Trim()).Where(x => x.Contains('/')).Select(x => x.Split('/')[0]).Distinct())
                    list.Add(new AppEntry(l, l.Split('.').Last(), false));
            }
            Apps = [.. list.OrderBy(a => a.Label, StringComparer.CurrentCulture)];
            LoadIcons(Apps.Select(a => a.Package));
        }
        catch (Exception e) { Log.Write("App 清單載入失敗 " + e.Message); }
        finally { Loading = false; Changed?.Invoke(); }
    }

    // MARK: 圖示

    private string IconDir => CacheDir.For(Device, "icons");

    /// <summary>UI 取圖示；還沒載入時回傳 null，載好後會觸發 <see cref="Changed"/></summary>
    public ImageSource? Icon(string pkg)
    {
        if (_icons.TryGetValue(pkg, out var img)) return img;
        var f = Path.Combine(IconDir, pkg + ".png");
        if (File.Exists(f)) return _icons[pkg] = new BitmapImage(new Uri(f));
        LoadIcons([pkg]);
        return null;
    }

    public void LoadIcons(IEnumerable<string> pkgs)
    {
        var need = new List<string>();
        foreach (var p in pkgs)
        {
            if (_icons.ContainsKey(p) || _iconRequests.Contains(p)) continue;
            if (File.Exists(Path.Combine(IconDir, p + ".png"))) { _icons[p] = new BitmapImage(new Uri(Path.Combine(IconDir, p + ".png"))); continue; }
            need.Add(p);
        }
        if (need.Count == 0) return;
        foreach (var p in need) _iconRequests.Add(p);
        var dir = IconDir;
        _ = Task.Run(async () =>
        {
            var got = new List<string>();
            // 分批，避免指令列過長
            foreach (var chunk in need.Chunk(40))
            {
                string outp;
                try { outp = await Helper(["icons", "128", .. chunk], 120); } catch { continue; }
                foreach (var line in outp.Split('\n'))
                {
                    var p = line.Trim().Split('\t');
                    if (p.Length != 2) continue;
                    try { File.WriteAllBytes(Path.Combine(dir, p[0] + ".png"), Convert.FromBase64String(p[1])); got.Add(p[0]); } catch { }
                }
            }
            _ui.TryEnqueue(() =>
            {
                foreach (var p in got) _icons[p] = new BitmapImage(new Uri(Path.Combine(dir, p + ".png")));
                Changed?.Invoke();
            });
        });
    }

    // MARK: 執行中／最近

    public async Task RefreshTasksAsync()
    {
        var dev = Device;
        if (_homePackage is null)
        {
            var h = await SafeShell(dev, "cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.HOME | tail -1");
            _homePackage = h.Split('/').FirstOrDefault()?.Trim() ?? "";
        }
        if (_installed.Count == 0)
        {
            var o = await SafeShell(dev, "pm list packages");
            _installed = o.Split('\n').Select(x => x.Replace("package:", "").Trim()).Where(x => x.Length > 0).ToHashSet();
        }
        var outp = await SafeShell(dev,
            "dumpsys activity recents; echo '@@PS@@'; ps -A -o NAME; echo '@@FG@@'; dumpsys activity activities | grep -m1 -E 'topResumedActivity=|mResumedActivity:'");
        if (outp.Length == 0) return;

        var fgParts = outp.Split("@@FG@@");
        var halves = fgParts[0].Split("@@PS@@");
        // 「…ActivityRecord{3f2a0 u0 com.instagram.android/.MainActivity t12…}」→ com.instagram.android
        string? fg = null;
        if (fgParts.Length > 1)
        {
            var m = Regex.Match(fgParts[1], @"u\d+ [A-Za-z0-9_.]+/");
            if (m.Success) fg = m.Value.Split(' ').Last().TrimEnd('/');
        }
        if (fg is not null && (fg == _homePackage || fg == "com.android.systemui")) fg = null;
        var procs = (halves.Length > 1 ? halves[1] : "").Split('\n').Select(x => x.Trim()).ToHashSet();

        var order = new List<string>();
        foreach (var (pkg, _) in ParseRecentTasks(halves[0], _installed))
            if (pkg != _homePackage && pkg != "com.android.systemui" && !order.Contains(pkg)) order.Add(pkg);
        var run = order.Where(procs.Contains).ToList();
        // 不在「最近使用」裡的 App 只要它在最上層，就一定是執行中
        if (fg is not null && !run.Contains(fg)) run.Insert(0, fg);
        var rec = order.Where(p => !procs.Contains(p) && p != fg).Take(12).ToList();

        bool changed = fg != Foreground || !run.SequenceEqual(Running) || !rec.SequenceEqual(Recent);
        Foreground = fg; Running = run; Recent = rec;
        LoadIcons(run.Concat(rec));
        if (changed) Changed?.Invoke();
    }

    private static async Task<string> SafeShell(Device d, string cmd)
    {
        try { return await d.Client.ShellAsync(cmd); } catch { return ""; }
    }

    /// <summary>
    /// 解析 dumpsys activity recents：每個「Recent #N」區塊找 realActivity／cmp=，找不到時用 affinity
    /// （A=uid:affinity）對已安裝套件做前綴比對（例如 com.android.settings.root）。另附每個區塊的 task id。
    /// </summary>
    public static List<(string Pkg, int? Task)> ParseRecentTasks(string text, HashSet<string> installed)
    {
        var result = new List<(string, int?)>();
        var blocks = new List<List<string>>();
        bool inBlock = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("* Recent #")) { blocks.Add([line]); inBlock = true; }
            else if (inBlock && line.StartsWith("    ") && !line.Contains("RecentTaskInfo")) blocks[^1].Add(line);
            else inBlock = false;   // 後面的「Visible recent tasks」等段落不屬於任何區塊
        }
        foreach (var block in blocks)
        {
            var header = block[0];
            if (header.Contains("type=home") || header.Contains("type=recents") || header.Contains("type=assistant")) continue;
            string? pkg = null;
            foreach (var l in block.Skip(1))
            {
                int k = l.IndexOf("realActivity={", StringComparison.Ordinal), klen = 14;
                if (k < 0) { k = l.IndexOf("realActivity=", StringComparison.Ordinal); klen = 13; }
                if (k < 0) { k = l.IndexOf("cmp=", StringComparison.Ordinal); klen = 4; }
                if (k < 0) continue;
                var rest = l[(k + klen)..];
                int slash = rest.IndexOf('/');
                if (slash >= 0) { pkg = rest[..slash]; break; }
            }
            if (pkg is null)
            {
                int a = header.IndexOf(" A=", StringComparison.Ordinal);
                if (a >= 0)
                {
                    var aff = new string(header[(a + 3)..].TakeWhile(ch => ch != ' ').ToArray());
                    int colon = aff.IndexOf(':');
                    if (colon >= 0) aff = aff[(colon + 1)..];
                    var comps = aff.Split('.', StringSplitOptions.RemoveEmptyEntries).ToList();
                    while (comps.Count >= 2)
                    {
                        var cand = string.Join('.', comps);
                        if (installed.Count == 0 || installed.Contains(cand)) { pkg = cand; break; }
                        comps.RemoveAt(comps.Count - 1);
                    }
                }
            }
            int? task = null;
            var tm = Regex.Match(header, @" #(\d+) ");
            if (tm.Success) task = int.Parse(tm.Groups[1].Value);
            if (pkg is not null) result.Add((pkg, task));
        }
        return result;
    }

    // MARK: 操作

    /// <summary>在指定 display 上啟動 App（null = 主螢幕）</summary>
    public async Task LaunchAsync(string pkg, int? displayId = null)
    {
        var comp = (await SafeShell(Device,
            $"cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER {pkg} | tail -1")).Trim();
        var display = displayId is int d ? $" --display {d}" : "";
        if (comp.Contains('/')) await SafeShell(Device, $"am start{display} -n {comp}");
        else await SafeShell(Device, $"monkey -p {pkg} -c android.intent.category.LAUNCHER 1");
    }

    /// <summary>強制停止，並把它在最近使用裡的 task 一起移除（常駐程序 force-stop 不會結束，task 也不會消失）</summary>
    public async Task ForceStopAsync(string pkg)
    {
        await SafeShell(Device, $"am force-stop {pkg}");
        var recents = await SafeShell(Device, "dumpsys activity recents");
        var ids = ParseRecentTasks(recents, _installed).Where(t => t.Pkg == pkg).Select(t => t.Task).OfType<int>().ToList();
        if (ids.Count > 0)
        {
            string outp = "";
            try { outp = await Helper(["removetasks", .. ids.Select(i => i.ToString())], 20); } catch { }
            if (!outp.Contains("\t1"))
                await SafeShell(Device, string.Join("; ", ids.Select(i => $"am stack remove {i}")));
        }
        await RefreshTasksAsync();
    }

    // MARK: 排序（總覽、App 列、啟動台共用）

    /// <summary>已開啟的在前（目前在最上層的 → 已開啟的喜好項目 → 其他已開啟的），還沒開的喜好項目在後</summary>
    public (List<string> Opened, List<string> Unopened) BarOrder(IReadOnlyList<string> pins)
    {
        var cur = Foreground;
        var run = Running;
        var opened = (cur is null ? [] : new List<string> { cur })
            .Concat(pins.Where(p => run.Contains(p) && p != cur))
            .Concat(run.Where(p => !pins.Contains(p) && p != cur)).ToList();
        var unopened = pins.Where(p => !run.Contains(p) && p != cur).ToList();
        return (opened, unopened);
    }

    /// <summary>總覽與啟動台的分區：「喜好項目」＝所有釘選的（已開啟的在前）；「已開啟」＝已開啟但沒加入喜好項目的</summary>
    public (List<string> Favorites, List<string> OpenOnly) Sections(IReadOnlyList<string> pins)
    {
        var cur = Foreground;
        var run = Running;
        var favorites = pins.Where(p => p == cur)
            .Concat(pins.Where(p => run.Contains(p) && p != cur))
            .Concat(pins.Where(p => !run.Contains(p) && p != cur)).ToList();
        var openOnly = (cur is not null && !pins.Contains(cur) ? [cur] : new List<string>())
            .Concat(run.Where(p => !pins.Contains(p) && p != cur)).ToList();
        return (favorites, openOnly);
    }
}
