using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using Windows.ApplicationModel.DataTransfer;

namespace PhoneCentre.Mirror;

/// <summary>一個鏡像視窗的狀態與工作階段管理（對應 MirrorModel.swift）：主螢幕鏡像，或是在虛擬顯示器上執行的單一 App／桌面。只在 UI 執行緒使用。</summary>
public sealed class MirrorModel
{
    public Device Device { get; set; }
    /// <summary>非 null 代表「獨立視窗 App」，在虛擬顯示器上執行</summary>
    public string? AppPackage { get; }
    /// <summary>虛擬顯示器的桌面模式（有系統裝飾，類似 DeX）</summary>
    public bool DesktopMode { get; }
    /// <summary>沒有 App 清單時，由呼叫端傳入的 App 名稱</summary>
    public string? AppLabel { get; set; }

    public bool IsAppWindow => AppPackage is not null;
    public bool IsVirtualDisplay => AppPackage is not null || DesktopMode;

    public string Title => AppPackage is { } p ? $"{AppLabel ?? Catalog.Label(p)} — {Device.DisplayName}"
        : DesktopMode ? $"桌面模式 — {Device.DisplayName}" : Device.DisplayName;

    // MARK: 狀態（改了之後都會觸發 Changed，視窗再重新整理畫面）

    public string? StatusText { get; private set; } = "連線中…";
    public string? Toast { get; private set; }
    public string MarkedText { get; set; } = "";
    public bool Locked { get; private set; }
    public bool ScreenOff { get; private set; }
    public bool Muted { get; private set; }
    public KeyboardMode KeyboardMode { get; private set; }
    public VideoOptions VideoOptions { get; private set; }
    public (int W, int H) FrameSize { get; private set; }
    /// <summary>桌面模式還沒開過任何 App</summary>
    public bool DesktopEmpty { get; set; } = true;
    public DeviceStatus Status { get; private set; } = new();
    /// <summary>第一次讀到手機狀態之前為 false（此時狀態環只轉圈圈，不顯示預設的錯誤資訊）</summary>
    public bool StatusLoaded { get; private set; }
    /// <summary>獨立視窗的 App 在手機上被關掉了（例如在主螢幕滑掉）</summary>
    public bool AppClosed { get; private set; }
    /// <summary>App 被關掉時顯示的最後一幀</summary>
    public (byte[] Bgra, int W, int H)? ClosedSnapshot { get; private set; }
    public bool ShowUnlockBanner => Locked && !_unlockDismissed && AppSettings.Data.OfferHelloUnlock;
    /// <summary>輸入法貼上文字期間，手機剪貼簿的回傳不要蓋掉電腦的剪貼簿</summary>
    public DateTime ClipboardGuardUntil { get; set; } = DateTime.MinValue;

    public event Action? Changed;
    /// <summary>有新的 session（換了解碼器）</summary>
    public event Action? LinkChanged;
    /// <summary>截圖白閃</summary>
    public event Action? FlashRequested;
    /// <summary>由視窗提供：關閉／隱藏／旋轉視窗、依影像大小調整視窗</summary>
    public Action? OnCloseWindow, OnHideWindow, OnRotateWindow;
    public Action<int, int>? OnFrameSize;
    /// <summary>由視窗提供：解鎖時用來顯示對話框的 XamlRoot</summary>
    public Func<Microsoft.UI.Xaml.XamlRoot?>? GetXamlRoot;
    /// <summary>這個視窗是否在最前面（鎖定畫面偵測只在前景視窗做）</summary>
    public Func<bool>? IsActive;

    public AppCatalog Catalog { get; }

    private sealed class Link
    {
        public required ScrcpySession Session;
        public required H264Decoder Decoder;
        public AudioPlayer? Audio;
        public readonly List<Action> Unhook = [];
        public void Detach() { foreach (var u in Unhook) u(); Unhook.Clear(); }
    }

    private Link? _link;
    private (Link Link, int? Display)? _retiring;
    public ScrcpySession? Session => _link?.Session;
    public H264Decoder? Decoder => _link?.Decoder;

    /// <summary>這個視窗的虛擬顯示器 id（主螢幕鏡像為 null）</summary>
    public int? DisplayId { get; private set; }
    /// <summary>已被某個鏡像視窗認領的虛擬顯示器，避免多個視窗大小相同時認錯</summary>
    private static readonly HashSet<int> ClaimedDisplays = [];

    private bool _userStopped, _unlockDismissed, _appSeen, _appWasRunning;
    private int _emptyChecks, _changeToken;
    private DateTime _lastSnapshotRequest = DateTime.MinValue;
    private string? _pendingStatus;
    private CancellationTokenSource? _retry;
    private readonly List<DispatcherQueueTimer> _timers = [];
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private bool _clipboardDirty;
    private (double W, double H, int Dpi)? _customDisplay;
    /// <summary>視窗所在螢幕的縮放比例（100% = 1），由視窗提供</summary>
    public double BackingScale { get; set; } = 1;

    public MirrorModel(Device device, string? appPackage = null, bool desktopMode = false)
    {
        Device = device; AppPackage = appPackage; DesktopMode = desktopMode;
        var d = AppSettings.Data;
        ScreenOff = appPackage is null && !desktopMode && d.TurnScreenOffWhileMirroring;
        KeyboardMode = d.KeyboardMode;
        VideoOptions = AppSettings.VideoOptionsFor(device.Key);
        Catalog = DeviceManager.Shared.Catalog(device);
        Clipboard.ContentChanged += OnClipboardChanged;
    }

    private void OnClipboardChanged(object? s, object e) => _clipboardDirty = true;
    private void Raise() => Changed?.Invoke();
    /// <summary>組字中文字變了（只重畫提示）</summary>
    public void ShowMarked() => Raise();

    public void SetKeyboardMode(KeyboardMode m)
    {
        KeyboardMode = m;
        AppSettings.Update(d => d.KeyboardMode = m);
        Raise();
    }

    // MARK: 連線

    /// <summary>handover = true：調整虛擬顯示器大小，舊 session 繼續撐著，新顯示器好了再把 App 搬過去</summary>
    public void Start(bool handover = false)
    {
        _userStopped = false;
        _retry?.Cancel();
        if (!handover) { StatusText = _pendingStatus ?? "連線中…"; _pendingStatus = null; Raise(); }
        var s = AppSettings.Data;
        var cfg = new MirrorConfig
        {
            Video = VideoOptions,
            // 同一台手機只讓主鏡像視窗傳音訊，避免重複
            Audio = s.AudioEnabled && !IsVirtualDisplay,
            MuteDevice = s.MuteDeviceWhenForwarding,
            ClipboardSync = s.ClipboardSync,
            ScreenOff = ScreenOff,
        };
        if (IsVirtualDisplay)
        {
            cfg.NewDisplay = VirtualDisplaySpec();
            cfg.SystemDecorations = DesktopMode;
            cfg.StartApp = handover ? null : AppPackage;
        }
        if (!handover && _retiring is null && AppPackage is { } pkg)
        {
            var c = Device.Client;
            _ = Task.Run(async () =>
            {
                string o = ""; try { o = await c.ShellAsync($"pidof {pkg}"); } catch { }
                _ui.TryEnqueue(() => _appWasRunning = o.Trim().Length > 0);
            });
        }
        bool hadOld;
        if (handover && _link is { } old)
        {
            old.Detach();
            _retiring = (old, DisplayId);
            _link = null;
            hadOld = false;
        }
        else hadOld = DetachCurrentSession();

        var link = NewLink(cfg);
        _link = link;
        LinkChanged?.Invoke();
        if (hadOld)
        {
            // 等舊的 server 釋放硬體編碼器
            _ = Task.Run(async () => { await Task.Delay(600); _ui.TryEnqueue(() => { if (_link == link) link.Session.Start(); }); });
        }
        else link.Session.Start();
        StartTimers();
    }

    private Link NewLink(MirrorConfig cfg)
    {
        var session = new ScrcpySession(Device, cfg);
        var decoder = new H264Decoder { KeepLastFrame = IsAppWindow };
        var link = new Link { Session = session, Decoder = decoder, Audio = cfg.Audio ? new AudioPlayer { Volume = Muted ? 0 : 1 } : null };

        Action<ScrcpySession.SessionState> onState = st => { if (_link == link) Handle(st); };
        Action<int, int> onSize = (w, h) =>
        {
            if (_link != link) return;
            var old = FrameSize;
            FrameSize = (w, h);
            // 長寬比或解析度改變時，畫面會短暫黑屏到新畫面出現；先顯示提示
            if (old != (0, 0) && old != (w, h) && StatusText is null) ShowChange("正在變更解析度…", 0.8);
            OnFrameSize?.Invoke(w, h);
            Raise();
        };
        Action<string> onClip = text =>
        {
            // 自動同步或 Ctrl+C／Ctrl+X 取得的手機剪貼簿 → 電腦（輸入法貼上文字期間不要蓋掉電腦的剪貼簿）
            if (_link != link || DateTime.Now <= ClipboardGuardUntil) return;
            try { var dp = new DataPackage(); dp.SetText(text); Clipboard.SetContent(dp); _clipboardDirty = false; } catch { }
        };
        Action<byte[]> onConfig = p => decoder.Config(p);
        Action<byte[], ulong, bool> onFrame = (p, _, key) => decoder.Decode(p, key);
        Action<byte[]> onAudio = p => link.Audio?.Push(p);
        session.StateChanged += onState; session.FrameSizeChanged += onSize; session.DeviceClipboard += onClip;
        session.VideoConfig += onConfig; session.VideoFrame += onFrame; session.AudioPacket += onAudio;
        link.Unhook.Add(() => { session.StateChanged -= onState; session.FrameSizeChanged -= onSize; session.DeviceClipboard -= onClip; });
        return link;
    }

    private bool DetachCurrentSession()
    {
        if (_link is not { } old) return false;
        old.Detach();
        CloseLink(old);
        _link = null;
        return true;
    }

    private static void CloseLink(Link l)
    {
        l.Session.Stop();
        l.Audio?.Dispose();
        // 解碼器可能還在被背景執行緒用到，晚一點再釋放
        _ = Task.Run(async () => { await Task.Delay(1500); l.Decoder.Dispose(); });
    }

    public void Restart()
    {
        Start();
    }

    public void Stop()
    {
        _userStopped = true;
        _retry?.Cancel();
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        Clipboard.ContentChanged -= OnClipboardChanged;
        if (_retiring is { } r) { CloseLink(r.Link); _retiring = null; }
        if (DisplayId is int d) { ClaimedDisplays.Remove(d); DisplayId = null; }
        DetachCurrentSession();
    }

    // MARK: 虛擬顯示器大小

    /// <summary>視窗大小（DIP）→ 虛擬顯示器（像素、dpi）。像素數 = 螢幕實際顯示得出的像素，密度 160 × 縮放，讓 1 個 DIP ≈ 1 個 Android dp</summary>
    private (double W, double H, int Dpi) DisplaySpec(double wDip, double hDip)
    {
        double scale = Math.Max(1, BackingScale);
        double w = wDip * scale, h = hDip * scale, dpi = 160 * scale;
        // 受「最大解析度」限制（不限制時最多 2560），縮小時密度一起降，介面比例不變
        double cap = VideoOptions.MaxSize > 0 ? VideoOptions.MaxSize : 2560;
        double longest = Math.Max(w, h);
        if (longest > cap) { double k = cap / longest; w *= k; h *= k; dpi *= k; }
        // 取 8 的倍數時無條件捨去，避免比視窗大幾個像素而被縮放
        return (Math.Max(240, Math.Floor(w / 8) * 8), Math.Max(240, Math.Floor(h / 8) * 8), Math.Max(80, (int)Math.Round(dpi)));
    }

    private string VirtualDisplaySpec()
    {
        (double W, double H, int Dpi) spec;
        if (_customDisplay is { } c) spec = c;
        else
        {
            var area = DisplayArea.Primary.WorkArea;
            double sw = area.Width / Math.Max(1, BackingScale), sh = area.Height / Math.Max(1, BackingScale);
            double w, h;
            if (DesktopMode) { w = sw * 0.75 - MirrorLayout.RailWidth; h = sh * 0.75 - MirrorLayout.ToolbarHeight; }   // 桌面模式：橫向，約螢幕的 75%
            else
            {
                // 單一 App：沿用手機長寬比，高度約螢幕的 80%
                var p = (Device.Info?.ScreenSize ?? "").Split('x').Select(x => double.TryParse(x, out var v) ? v : 0).ToArray();
                double aspect = p.Length == 2 && p[0] > 0 && p[1] > 0 ? Math.Min(p[0], p[1]) / Math.Max(p[0], p[1]) : 0.47;
                h = sh * 0.8 - MirrorLayout.ToolbarHeight; w = h * aspect;
            }
            spec = DisplaySpec(w, h);
        }
        return $"{(int)spec.W}x{(int)spec.H}/{spec.Dpi}";
    }

    /// <summary>依還原的視窗大小建立虛擬顯示器（第一次連線前呼叫）</summary>
    public void PresetDisplay(double wDip, double hDip) => _customDisplay = DisplaySpec(wDip, hDip);

    /// <summary>使用者放開視窗邊框：依新的大小重建虛擬顯示器</summary>
    public void ResizeVirtualDisplay(double wDip, double hDip)
    {
        if (!IsVirtualDisplay || wDip < 50 || hDip < 50) return;
        var spec = DisplaySpec(wDip, hDip);
        var current = _customDisplay is { } c ? (c.W, c.H) : (FrameSize.W, FrameSize.H);
        if (Math.Abs(current.Item1 - spec.W) < 16 && Math.Abs(current.Item2 - spec.H) < 16) return;
        _customDisplay = spec;
        if (DisplayId is not null && Session?.State.Kind == ScrcpySession.StateKind.Running)
        {
            ShowChange("正在調整視窗大小…", 8);
            Start(handover: true);
        }
        else { _pendingStatus = "正在調整視窗大小…"; Start(); }
    }

    /// <summary>虛擬顯示器旋轉：寬高對調</summary>
    public void RotateVirtualDisplay() { ShowChange("正在變更方向…"); OnRotateWindow?.Invoke(); }

    // MARK: 虛擬顯示器

    private static int? FirstInt(string s, string marker = "")
    {
        var rest = s;
        if (marker.Length > 0) { int i = s.IndexOf(marker, StringComparison.Ordinal); if (i < 0) return null; rest = s[(i + marker.Length)..]; }
        var digits = new string(rest.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var v) ? v : null;
    }

    private async Task<string> Sh(string cmd, double timeout = 15) { try { return await Device.Client.ShellAsync(cmd, timeout); } catch { return ""; } }

    /// <summary>連線完成後找出自己的虛擬顯示器；調整大小的話就把 App 從舊顯示器搬過來再關掉舊的</summary>
    private async Task FinishConnectAsync()
    {
        var old = _retiring;
        var mine = await LocateDisplayAsync();
        if (old?.Display is int od) ClaimedDisplays.Remove(od);
        if (mine is int m) ClaimedDisplays.Add(m);
        DisplayId = mine;
        if (old is null && AppPackage is { } app && mine is int md) _ = EnsureAppOnDisplayAsync(app, md);
        if (old is not { } o) return;
        _retiring = null;
        if (o.Display is int from && mine is int to)
        {
            // 搬完要確認舊顯示器上已經沒有 task 才關舊 session，不然 App 會掉回手機主螢幕（佔住主螢幕一段時間）
            for (int i = 0; i < 4; i++)
            {
                var tasks = await RootTasksAsync(from);
                if (tasks.Count == 0) break;
                foreach (var t in tasks) await Sh($"am display move-stack {t} {to}");
                await Task.Delay(250);
            }
            await Task.Delay(400);
            await NudgeDisplayConfigAsync(to);
        }
        else if (AppPackage is { } a) Session?.Send(ControlMessage.StartApp(a));
        await Task.Delay(400);
        CloseLink(o.Link);
    }

    /// <summary>讓虛擬顯示器上的 App 重新排版：把密度加 1 再改回來，等於觸發一次螢幕設定變更</summary>
    private async Task NudgeDisplayConfigAsync(int d)
    {
        // 顯示器不存在（例如剛被關掉）時，`wm density -d <id>` 會退回去操作主螢幕，把主螢幕的密度改掉再改回，
        // 主螢幕就會重排。所以先確認這個虛擬顯示器真的還在，而且絕不碰主螢幕（0）。
        if (d == 0 || !await DisplayExistsAsync(d)) return;
        var outp = await Sh($"wm density -d {d}");
        var line = outp.Split('\n').LastOrDefault(l => l.Contains("density:"));
        if (line is null || !int.TryParse(line.Split(':').Last().Trim(), out var dpi)) return;
        await Sh($"wm density {dpi + 1} -d {d}");
        await Task.Delay(250);
        await Sh($"wm density {dpi} -d {d}");
    }

    private async Task<bool> DisplayExistsAsync(int d)
    {
        var o = await Sh($"dumpsys display | grep -c 'DisplayInfo{{\"scrcpy\", displayId {d}\"'");
        return int.TryParse(o.Trim(), out var n) && n > 0;
    }

    /// <summary>把 App 重新啟動（強制停止後在這個顯示器重開），讓它依這個顯示器的密度重新繪製</summary>
    private async Task RedrawAppAsync(string pkg)
    {
        await Sh($"am force-stop {pkg}");
        await Task.Delay(500);
        Session?.Send(ControlMessage.StartApp(pkg));
    }

    /// <summary>啟動後確認 App 真的在這個顯示器上。App 已經在主螢幕（或別的顯示器）執行時，啟動指令可能只是把原本的 task 叫到前面，這邊的畫面會一直是黑的：找到那個 task 搬過來</summary>
    private async Task EnsureAppOnDisplayAsync(string pkg, int display)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(2500);
            if (_retiring is not null || _userStopped || DisplayId != display) return;
            if ((await RootTasksAsync(display)).Count > 0)
            {
                if (_appWasRunning) { _appWasRunning = false; await RedrawAppAsync(pkg); } else await NudgeDisplayConfigAsync(display);
                return;
            }
            var outp = await Sh("am stack list | grep -E '^RootTask id=|taskId='", 10);
            int? root = null, rootDisplay = null, found = null;
            foreach (var line in outp.Split('\n'))
            {
                if (line.StartsWith("RootTask id=")) { root = FirstInt(line, "RootTask id="); rootDisplay = FirstInt(line, "displayId="); }
                else if (line.Contains("taskId=") && line.Contains($": {pkg}/") && root is not null && rootDisplay != display) found = root;
            }
            if (found is int f)
            {
                await Sh($"am display move-stack {f} {display}");
                await Task.Delay(600);
                _appWasRunning = false;
                await RedrawAppAsync(pkg);
            }
            else Session?.Send(ControlMessage.StartApp(pkg));
        }
    }

    /// <summary>關閉虛擬顯示器視窗前：把顯示器上的 App 送到主螢幕的背景。由手機端輔助程式（tobg）在同一個程序裡搬移後立刻把主螢幕原本最上層的 task 拉回來，失敗才退回 am 指令。</summary>
    public async Task SendAppsToBackgroundAsync()
    {
        if (!IsVirtualDisplay || DisplayId is not int d) return;
        var tasks = await RootTasksAsync(d);
        if (tasks.Count == 0) return;
        var outp = await Sh("dumpsys activity activities | grep -E 'Display #|mResumedActivity|taskId='");
        bool inMain = false; string? prev = null; int? prevTask = null;
        foreach (var line in outp.Split('\n'))
        {
            if (line.Contains("Display #")) { inMain = line.Contains("Display #0 "); continue; }
            if (!inMain) continue;
            if (prevTask is null && line.Trim().StartsWith("taskId=")) prevTask = FirstInt(line, "taskId=");
            if (prev is null && line.Contains("mResumedActivity"))
            {
                int i = line.IndexOf(" u0 ", StringComparison.Ordinal);
                if (i >= 0) prev = new string(line[(i + 4)..].TakeWhile(ch => ch != ' ' && ch != '}').ToArray());
            }
        }
        string moved = "";
        try { moved = await HelperTool.Shared.RunAsync(Device, ["tobg", (prevTask ?? -1).ToString(), .. tasks.Select(t => t.ToString())], 10); } catch { }
        string script;
        if (moved.Contains("ok")) script = prevTask is null && prev is not null ? $"am start -n {prev} --activity-reorder-to-front" : "";
        else
        {
            script = string.Join("; ", tasks.Select(t => $"am display move-stack {t} 0"));
            if (!string.IsNullOrEmpty(prev)) script += $"; am start -n {prev} --activity-reorder-to-front";
        }
        // App 回到背景後把它的程序收掉：下次在主螢幕打開時才會依主螢幕的密度重新繪製（不然會變超小）
        if (AppPackage is { } app) script += (script.Length == 0 ? "" : "; ") + $"am kill {app}";
        if (script.Length > 0) await Sh(script, 10);
    }

    /// <summary>依大小與認領狀態找出這個視窗的虛擬顯示器（取 id 最大 = 最新建立的）</summary>
    private async Task<int?> LocateDisplayAsync()
    {
        var want = FrameSize;
        var outp = await Sh("dumpsys display | grep 'mBaseDisplayInfo=DisplayInfo{\"scrcpy\"' | cut -c1-260");
        int? best = null;
        foreach (var line in outp.Split('\n'))
        {
            if (FirstInt(line, "displayId ") is not int id) continue;
            if (ClaimedDisplays.Contains(id) && !(id == DisplayId && _retiring is null)) continue;
            int r = line.IndexOf("real ", StringComparison.Ordinal);
            if (r < 0 || FirstInt(line[(r + 5)..]) is not int w) continue;
            int hr = line.IndexOf(" x ", r, StringComparison.Ordinal);
            if (hr < 0 || FirstInt(line[(hr + 3)..]) is not int h) continue;
            if (want == (0, 0) || (Math.Abs(w - want.W) < 2 && Math.Abs(h - want.H) < 2)) best = Math.Max(best ?? 0, id);
        }
        return best;
    }

    /// <summary>某個顯示器上的 root task id</summary>
    private async Task<List<int>> RootTasksAsync(int id)
    {
        var outp = await Sh("dumpsys activity activities | grep -E 'Display #|rootTaskId='", 15);
        bool inDisplay = false; var tasks = new List<int>();
        foreach (var line in outp.Split('\n'))
        {
            if (line.Contains("Display #")) { inDisplay = line.Contains($"Display #{id} "); continue; }
            if (inDisplay && line.Trim().StartsWith("taskId=") && FirstInt(line, "rootTaskId=") is int t && !tasks.Contains(t)) tasks.Add(t);
        }
        return tasks;
    }

    private void Handle(ScrcpySession.SessionState st)
    {
        switch (st.Kind)
        {
            case ScrcpySession.StateKind.Starting: if (_retiring is null && StatusText is null) { StatusText = "連線中…"; Raise(); } break;
            case ScrcpySession.StateKind.Running:
                StatusText = null; Raise();
                if (IsVirtualDisplay) _ = FinishConnectAsync();
                break;
            case ScrcpySession.StateKind.Failed:
                StatusText = $"連線中斷：{st.Message}"; Raise();
                ScheduleRetry();
                break;
            case ScrcpySession.StateKind.Stopped:
                if (!_userStopped) { StatusText = "連線中斷"; Raise(); ScheduleRetry(); }
                break;
        }
    }

    /// <summary>自動維持連線：裝置回來就重連</summary>
    private void ScheduleRetry()
    {
        if (_userStopped || !AppSettings.KeepAlive) return;
        _retry?.Cancel();
        var cts = _retry = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            for (int attempt = 1; attempt <= 600; attempt++)
            {
                try { await Task.Delay(3000, cts.Token); } catch { return; }
                if (_userStopped) return;
                _ui.TryEnqueue(() => { StatusText = $"連線中斷，等待裝置重新連線…（第 {attempt} 次）"; Raise(); });
                if (await DeviceIsOnlineAsync()) { _ui.TryEnqueue(() => { if (!cts.IsCancellationRequested) Start(); }); return; }
            }
        });
    }

    private async Task<bool> DeviceIsOnlineAsync()
    {
        var ep = Device.Endpoint; var serial = Device.Serial;
        try
        {
            var text = await Task.Run(() => AdbSocket.HostQuery(ep, "host:devices-l"));
            return Adb.Device.Parse(text, ep).Any(d => d.Serial == serial && d.IsOnline);
        }
        catch { return false; }
    }

    /// <summary>畫質、解析度、方向、視窗大小變更時，黑屏期間顯示的提示（連線好了會自動消失，最久 seconds 秒）</summary>
    public void ShowChange(string text, double seconds = 5)
    {
        int token = ++_changeToken;
        StatusText = text; Raise();
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds));
            _ui.TryEnqueue(() => { if (_changeToken == token && StatusText == text) { StatusText = null; Raise(); } });
        });
    }

    public void PendingChange(string text) => _pendingStatus = text;

    // MARK: 定時工作：剪貼簿、鎖定畫面、App 列

    private void Every(double seconds, Action a)
    {
        var t = _ui.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(seconds);
        t.Tick += (_, _) => a();
        t.Start();
        _timers.Add(t);
    }

    private void StartTimers()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        Every(0.7, () => _ = SyncClipboardAsync());
        Every(5, () => _ = RefreshStatusAsync());
        _ = RefreshStatusAsync();
        if (IsAppWindow) Every(2, () => _ = CheckAppAliveAsync());
        if (IsVirtualDisplay)
        {
            // 手機鎖定時虛擬顯示器上的 App 會黑屏，要明確提示
            Every(3, async () =>
            {
                bool l = await LockScreenHelper.IsLockedAsync(Device.Client);
                if (l != Locked) { Locked = l; if (!l) _unlockDismissed = false; Raise(); }
            });
        }
        // 主鏡像與桌面模式都有右側 App 列，需要定時更新
        if (!IsAppWindow) { Every(3, () => _ = PollStatusAsync()); _ = PollStatusAsync(); }
    }

    private async Task SyncClipboardAsync()
    {
        if (!AppSettings.Data.ClipboardSync || Session is not { State.Kind: ScrcpySession.StateKind.Running } s || !_clipboardDirty) return;
        _clipboardDirty = false;
        try
        {
            var content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text)) s.SetClipboard(await content.GetTextAsync(), false);
        }
        catch { }
    }

    /// <summary>虛擬顯示器上已經沒有任何 task：App 被關掉了。連續兩次都沒有才算，避免啟動中的空檔誤判</summary>
    private async Task CheckAppAliveAsync()
    {
        if (_retiring is not null || Session is not { State.Kind: ScrcpySession.StateKind.Running } || DisplayId is not int d) return;
        bool empty = (await RootTasksAsync(d)).Count == 0;
        if (!empty)
        {
            _appSeen = true; _emptyChecks = 0;
            if (AppClosed) { AppClosed = false; ClosedSnapshot = null; Raise(); }
            // 畫面靜止時編碼器不會產生新的關鍵幀，所以定期要一張，App 被關掉時才有最後一幀可以霧化
            if ((DateTime.Now - _lastSnapshotRequest).TotalSeconds > 6) { _lastSnapshotRequest = DateTime.Now; Session?.Send(ControlMessage.ResetVideo()); }
            return;
        }
        // 已顯示「已關閉」，但使用者在手機上自己把這個 App 重新打開了（跑在主螢幕）：搬回這個視窗並收起提示
        if (AppClosed && AppPackage is { } app && await TopResumedPackageAsync() == app)
        {
            AppClosed = false; ClosedSnapshot = null; _appSeen = false; _emptyChecks = 0; Raise();
            await EnsureAppOnDisplayAsync(app, d);
            return;
        }
        _emptyChecks++;
        if (_appSeen && _emptyChecks >= 2 && !AppClosed)
        {
            ClosedSnapshot = Decoder?.LastGoodFrame;
            AppClosed = true; Raise();
        }
    }

    /// <summary>目前在手機最上層（resumed）的 App 套件名</summary>
    private async Task<string?> TopResumedPackageAsync()
    {
        var outp = await Sh("dumpsys activity activities | grep -m1 -E 'topResumedActivity=|mResumedActivity:'");
        var m = System.Text.RegularExpressions.Regex.Match(outp, @"u\d+ [A-Za-z0-9_.]+/");
        return m.Success ? m.Value.Split(' ').Last().TrimEnd('/') : null;
    }

    public void ReopenApp()
    {
        if (AppPackage is not { } app) return;
        AppClosed = false; _appSeen = false; _emptyChecks = 0; Raise();
        Session?.Send(ControlMessage.StartApp(app));
        if (DisplayId is int d) _ = Task.Run(async () => { await Task.Delay(1500); await NudgeDisplayConfigAsync(d); });
    }

    private async Task RefreshStatusAsync()
    {
        if (await DeviceStatus.FetchAsync(Device.Client) is { } s)
        {
            if (!s.SameAs(Status)) Status = s;
            StatusLoaded = true; Raise();
        }
    }

    private async Task PollStatusAsync()
    {
        await Catalog.RefreshTasksAsync();
        // 鎖定畫面只會出現在主螢幕
        if (DesktopMode || !AppSettings.Data.OfferHelloUnlock || IsActive?.Invoke() != true) return;
        bool l = await LockScreenHelper.IsLockedAsync(Device.Client);
        if (l != Locked) { Locked = l; if (!l) _unlockDismissed = false; Raise(); }
    }

    public void DismissUnlock() { _unlockDismissed = true; Raise(); }

    public async Task UnlockAsync()
    {
        ShowToast("正在解鎖…", false);
        bool ok = await LockScreenHelper.UnlockAsync(Device, IsVirtualDisplay ? null : Session, ScreenOff, GetXamlRoot?.Invoke());
        Toast = ok ? null : "解鎖未成功，請確認儲存的密碼";
        if (ok) Locked = false;
        Raise();
        ClearToastLater();
    }

    // MARK: 操作

    public void ShowToast(string text, bool clear = true) { Toast = text; Raise(); if (clear) ClearToastLater(); }

    private void ClearToastLater()
    {
        var current = Toast;
        _ = Task.Run(async () => { await Task.Delay(4000); _ui.TryEnqueue(() => { if (Toast == current) { Toast = null; Raise(); } }); });
    }

    public void Key(int code)
    {
        Session?.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Down, code));
        Session?.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Up, code));
    }

    private async void InputKey(int code, int display) => await Sh($"input -d {display} keyevent {code}");

    /// <summary>返回：虛擬顯示器上要指定顯示器，不然會作用在主螢幕</summary>
    public void NavBack() { if (IsVirtualDisplay && DisplayId is int d) InputKey(KeyMap.Back, d); else Key(KeyMap.Back); }

    /// <summary>主畫面：主螢幕鏡像是 Home；獨立視窗＝隱藏視窗（App 留在背景）；桌面模式送 Home 到該顯示器</summary>
    public void NavHome()
    {
        if (!IsVirtualDisplay) { Key(KeyMap.Home); return; }
        if (IsAppWindow) OnHideWindow?.Invoke(); else if (DisplayId is int d) InputKey(KeyMap.Home, d);
    }

    /// <summary>最近使用：主螢幕走 scrcpy；桌面模式把 APP_SWITCH 送到該顯示器</summary>
    public void NavRecents() { if (IsVirtualDisplay && DisplayId is int d) InputKey(KeyMap.AppSwitch, d); else Key(KeyMap.AppSwitch); }

    /// <summary>強制停止 App 並關閉視窗</summary>
    public async void ForceStopAndClose()
    {
        if (AppPackage is not { } pkg) return;
        await Catalog.ForceStopAsync(pkg);
        OnCloseWindow?.Invoke();
    }

    /// <summary>結束程式前：獨立 App 視窗跟導覽列的「結束」一樣強制停止 App；桌面模式把 App 送到背景。不處理的話虛擬顯示器一關，App 會被系統搬回手機主螢幕</summary>
    public async Task PrepareForQuitAsync()
    {
        if (AppPackage is { } pkg) await Catalog.ForceStopAsync(pkg);
        else if (DesktopMode) await SendAppsToBackgroundAsync();
    }

    /// <summary>鎖定方向：0 = 0°、1 = 90°、2 = 180°、3 = 270°</summary>
    public async void SetRotation(int r)
    {
        ShowChange("正在變更方向…");
        Status.AutoRotate = false; Status.UserRotation = r;
        await Sh($"settings put system accelerometer_rotation 0; settings put system user_rotation {r}");
    }

    /// <summary>強制所有 App 使用設定的方向：忽略 App 自己要求的方向，並讓畫面固定跟著使用者設定的旋轉。關閉就恢復各 App 自己決定</summary>
    public async void SetForceOrientation(bool on)
    {
        ShowChange("正在變更方向…");
        Status.ForceOrientation = on;
        await Sh($"wm set-ignore-orientation-request {(on ? "true" : "false")}; wm fixed-to-user-rotation {(on ? "enabled" : "default")}");
    }

    public async void SetAutoRotate(bool on)
    {
        Status.AutoRotate = on;
        await Sh($"settings put system accelerometer_rotation {(on ? 1 : 0)}");
    }

    public void ToggleScreenOff()
    {
        ScreenOff = !ScreenOff;
        Session?.Send(ControlMessage.SetDisplayPower(!ScreenOff));
        Raise();
    }

    public void ToggleMute()
    {
        Muted = !Muted;
        if (_link?.Audio is { } a) a.Volume = Muted ? 0 : 1;
        Raise();
    }

    public void Launch(string pkg) { DesktopEmpty = false; Session?.Send(ControlMessage.StartApp(pkg)); }

    /// <summary>截圖：先白閃，存到桌面</summary>
    public async void Screenshot()
    {
        FlashRequested?.Invoke();
        var png = await CaptureScreenPngAsync();
        if (png is null) { ShowToast("截圖失敗"); return; }
        var name = $"{Device.DisplayName} 截圖 {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png";
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
        try { await File.WriteAllBytesAsync(path, png); } catch { ShowToast("截圖失敗"); return; }
        ShowToast($"已存到桌面：{name}");
    }

    private static bool IsPng(byte[] d) => d.Length > 100 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;

    /// <summary>主螢幕用 screencap；虛擬顯示器（或 screencap 失敗，例如 Android 14 對虛擬顯示器的 -d 無效）改取鏡像影像的關鍵幀</summary>
    private async Task<byte[]?> CaptureScreenPngAsync()
    {
        try
        {
            if (!IsVirtualDisplay) { var d = await Device.Client.ExecAsync("screencap -p", 20); if (IsPng(d)) return d; }
            else if (DisplayId is int id) { var d = await Device.Client.ExecAsync($"screencap -d {id} -p", 20); if (IsPng(d)) return d; }
        }
        catch { }
        if (Decoder is not { } dec || Session is not { } s) return null;
        var tcs = new TaskCompletionSource<(byte[], int, int)?>();
        dec.CaptureNextKeyFrame((b, w, h) => tcs.TrySetResult((b, w, h)));
        s.Send(ControlMessage.ResetVideo());
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        if (done != tcs.Task || tcs.Task.Result is not { } f) return null;
        return await PngEncoder.EncodeAsync(f.Item1, f.Item2, f.Item3);
    }

    public void ApplyVideoOptions(VideoOptions o)
    {
        VideoOptions = o;
        AppSettings.SetVideoOptions(o, Device.Key);
        _pendingStatus = "正在變更畫質設定…";
        Restart();
    }

    public async void Drop(IReadOnlyList<string> paths)
    {
        await DropHandler.HandleAsync(paths, Device, m => ShowToast(m, false));
        if (paths.Any(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase))) await Catalog.ReloadAsync();
        ClearToastLater();
    }
}
