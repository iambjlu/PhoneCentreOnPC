using System.Text.Json;
using Microsoft.UI.Dispatching;
using PhoneCentre.Adb;
using PhoneCentre.Settings;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

/// <summary>簡訊（content://sms）</summary>
public sealed class PhoneSMS
{
    public int Id { get; set; }
    public string Address { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    /// <summary>1 收到、2 寄出，其他（草稿、發送中等）</summary>
    public int Type { get; set; } = 1;
    public bool Read { get; set; }
    public string Body { get; set; } = "";
    public bool IsSent => Type != 1;
}

/// <summary>通話記錄（content://call_log/calls）</summary>
public sealed class PhoneCall
{
    public int Id { get; set; }
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset Date { get; set; }
    public int Duration { get; set; }      // 秒
    /// <summary>1 撥入、2 撥出、3 未接、4 語音信箱、5 拒接、6 封鎖</summary>
    public int Type { get; set; }
    public string TypeTitle => Type switch { 1 => "撥入", 2 => "撥出", 3 => "未接", 4 => "語音信箱", 5 => "已拒接", 6 => "已封鎖", _ => "其他" };
    public string Icon => Type switch { 1 => "call_received", 2 => "call_made", 3 or 5 or 6 => "call_missed", _ => "call" };
    public bool IsMissed => Type is 3 or 5 or 6;
}

/// <summary>通知。<see cref="ClearedAt"/> 為 null 表示手機上還在；有值表示已被清掉（仍在保存期限內）</summary>
public sealed class PhoneNotification
{
    public string Id { get; set; } = "";
    /// <summary>手機端的通知 key（含套件、id、tag）</summary>
    public string Key { get; set; } = "";
    public string Package { get; set; } = "";
    public string Title { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>常駐通知（前景服務等）</summary>
    public bool Ongoing { get; set; }
    public DateTimeOffset FirstSeen { get; set; }
    public DateTimeOffset? ClearedAt { get; set; }
    public bool IsActive => ClearedAt is null;
}

/// <summary>一台裝置的所有已保存資料</summary>
public sealed class PhoneDataBundle
{
    public List<PhoneSMS> Sms { get; set; } = [];
    public List<PhoneCall> Calls { get; set; } = [];
    public List<PhoneNotification> Notifications { get; set; } = [];
    public DateTimeOffset? LastSync { get; set; }
}

// MARK: - 解析 adb 輸出

public static class PhoneDataParser
{
    public static readonly string[] SmsKeys = ["_id", "address", "date", "type", "read", "body"];
    public static readonly string[] CallKeys = ["_id", "number", "name", "date", "duration", "type"];

    /// <summary>
    /// `content query` 的輸出：每筆以「Row: N 」開頭，欄位為 `key=value, key=value`；
    /// 值可能含逗號與換行，所以依投影欄位的固定順序切割（最後一欄可含任何內容）。
    /// </summary>
    public static List<Dictionary<string, string>> Rows(string text, string[] keys)
    {
        var raws = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Row: "))
            {
                int sp = line.IndexOf(' ', 5);
                if (sp >= 0) raws.Add(line[(sp + 1)..]);
            }
            else if (raws.Count > 0) raws[^1] += "\n" + line;
        }
        var result = new List<Dictionary<string, string>>();
        foreach (var raw in raws)
        {
            int cursor = 0;
            var starts = new List<(string Key, int Marker, int Value)>();
            bool ok = true;
            for (int i = 0; i < keys.Length; i++)
            {
                var needle = (i == 0 ? "" : ", ") + keys[i] + "=";
                int idx = raw.IndexOf(needle, cursor, StringComparison.Ordinal);
                if (idx < 0) { ok = false; break; }
                starts.Add((keys[i], idx, idx + needle.Length));
                cursor = idx + needle.Length;
            }
            if (!ok) continue;
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < starts.Count; i++)
            {
                int end = i + 1 < starts.Count ? starts[i + 1].Marker : raw.Length;
                var v = raw[starts[i].Value..end];
                if (i + 1 == starts.Count) v = v.Trim('\n', '\r');
                dict[starts[i].Key] = v == "NULL" ? "" : v;
            }
            result.Add(dict);
        }
        return result;
    }

    public static List<PhoneSMS> Sms(string text) => Rows(text, SmsKeys).Select(r =>
        int.TryParse(r["_id"], out var id) && long.TryParse(r["date"], out var ms)
            ? new PhoneSMS { Id = id, Address = r["address"], Date = DateTimeOffset.FromUnixTimeMilliseconds(ms), Type = int.TryParse(r["type"], out var t) ? t : 1, Read = r["read"] == "1", Body = r["body"] }
            : null).OfType<PhoneSMS>().ToList();

    public static List<PhoneCall> Calls(string text) => Rows(text, CallKeys).Select(r =>
        int.TryParse(r["_id"], out var id) && long.TryParse(r["date"], out var ms)
            ? new PhoneCall { Id = id, Number = r["number"], Name = r["name"], Date = DateTimeOffset.FromUnixTimeMilliseconds(ms), Duration = int.TryParse(r["duration"], out var d) ? d : 0, Type = int.TryParse(r["type"], out var t) ? t : 0 }
            : null).OfType<PhoneCall>().ToList();

    public sealed class ActiveNotification
    {
        public string Key = "", Package = "", Title = "", Text = "";
        public bool Ongoing;
    }

    /// <summary>`dumpsys notification --noredact` 的「Notification List」區段。找不到這個區段（指令失敗）時回傳 null，避免把所有通知誤判成已清除。</summary>
    public static List<ActiveNotification>? Notifications(string dump)
    {
        bool inList = false, found = false;
        var outList = new List<ActiveNotification>();
        ActiveNotification? cur = null;
        string? title = null, text = null, bigText = null;

        void Finish()
        {
            if (cur is null) return;
            cur.Title = title ?? "";
            cur.Text = (!string.IsNullOrEmpty(bigText) ? bigText : text) ?? "";
            outList.Add(cur);
            cur = null; title = text = bigText = null;
        }
        void Assign(string name, string v)
        {
            switch (name) { case "title": title = v; break; case "text": text = v; break; default: bigText = v; break; }
        }

        // 多行的 extras 值（文字含換行）：開頭有「(」但行尾沒有「)」，累積到出現行尾的「)」為止
        (string Name, string Buf)? open = null;

        foreach (var raw in dump.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (!inList)
            {
                if (line.StartsWith("  Notification List:")) { inList = true; found = true; }
                continue;
            }
            if (open is { } o)
            {
                var buf = o.Buf + "\n" + line;
                if (line.EndsWith(')')) { Assign(o.Name, buf[..^1]); open = null; } else open = (o.Name, buf);
                continue;
            }
            // 區段結束：縮排剛好 2 格的下一個標題
            if (line.StartsWith("  ") && line.Length >= 3 && line[2] != ' ') { Finish(); break; }
            var t = line.Trim(' ', '\t');
            if (t.StartsWith("NotificationRecord("))
            {
                Finish();
                var pkg = Value(t, "pkg=");
                var key = Value(t, "key=", ": Notification(");
                if (pkg is null || key is null) continue;
                var flagsStr = Value(t, "flags=0x");
                int flags = flagsStr is not null && int.TryParse(flagsStr, System.Globalization.NumberStyles.HexNumber, null, out var fl) ? fl : 0;
                cur = new ActiveNotification { Key = key, Package = pkg, Ongoing = (flags & 0x2) != 0 };
            }
            else if (cur is not null && t.StartsWith("android."))
            {
                foreach (var (field, name) in new[] { ("android.title", "title"), ("android.text", "text"), ("android.bigText", "bigText") })
                {
                    if (!t.StartsWith(field + "=")) continue;
                    int paren = t.IndexOf('(');
                    if (paren < 0 || !t[..paren].Contains("String")) break;
                    var rest = t[(paren + 1)..];
                    if (t.EndsWith(')')) Assign(name, rest[..^1]); else open = (name, rest);
                    break;
                }
            }
        }
        Finish();
        return found ? outList : null;
    }

    private static string? Value(string s, string marker, string? until = null)
    {
        int i = s.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return null;
        var rest = s[(i + marker.Length)..];
        if (until is not null)
        {
            int e = rest.IndexOf(until, StringComparison.Ordinal);
            if (e >= 0) return rest[..e];
        }
        return new string(rest.TakeWhile(c => c != ' ').ToArray());
    }
}

// MARK: - 保存與定時收集

/// <summary>
/// 簡訊、通知、通話記錄的本機保存與定時收集（全程用 adb shell，不在手機安裝任何東西）。
/// 儲存在 %LOCALAPPDATA%\PhoneCentre\Data\&lt;裝置鍵&gt;\data.json（不放快取目錄，不會被容量清理刪掉）。
/// </summary>
public sealed class PhoneDataStore
{
    public static PhoneDataStore Shared { get; } = new();

    private readonly Dictionary<string, PhoneDataBundle> _bundles = new();
    private readonly HashSet<string> _syncing = [];
    private DispatcherQueue? _ui;
    private DispatcherQueueTimer? _timer;
    private int _tick;

    /// <summary>已保存的資料有變動</summary>
    public event Action? Changed;
    /// <summary>同步開始或結束（給「立即更新」的轉圈圈用）</summary>
    public event Action? SyncStateChanged;

    private static string Root
    {
        get { var d = Path.Combine(Assets.SupportDir, "Data"); Directory.CreateDirectory(d); return d; }
    }
    private static string FileFor(string key) => Path.Combine(Root, key, "data.json");

    public bool IsSyncing(Device d) => _syncing.Contains(d.Key);

    public PhoneDataBundle Bundle(Device d) => Load(d.Key);

    private PhoneDataBundle Load(string key)
    {
        if (_bundles.TryGetValue(key, out var b)) return b;
        try { b = JsonSerializer.Deserialize<PhoneDataBundle>(File.ReadAllText(FileFor(key))) ?? new(); }
        catch { b = new(); }
        return _bundles[key] = b;
    }

    private void Save(string key, PhoneDataBundle b)
    {
        _bundles[key] = b;
        try
        {
            var f = FileFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            var tmp = f + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(b));
            File.Move(tmp, f, true);
        }
        catch (Exception e) { Log.Write("保存手機資料失敗 " + e.Message); }
    }

    /// <summary>刪除這台裝置所有已保存的資料</summary>
    public void DeleteAll(Device d)
    {
        _bundles.Remove(d.Key);
        try { Directory.Delete(Path.Combine(Root, d.Key), true); } catch { }
        Changed?.Invoke();
    }

    // MARK: 保存期限（0 = 永久）

    private static DateTimeOffset? Cutoff(int days) => days > 0 ? DateTimeOffset.Now.AddDays(-days) : null;

    /// <summary>依設定刪掉超過保存期限的資料（還在手機上的通知不刪）</summary>
    public void Prune()
    {
        bool any = false;
        foreach (var dir in Directory.GetDirectories(Root))
        {
            var key = Path.GetFileName(dir);
            var b = Load(key);
            int before = b.Sms.Count + b.Calls.Count + b.Notifications.Count;
            if (Cutoff(AppSettings.SmsRetentionDays) is { } cs) b.Sms.RemoveAll(x => x.Date < cs);
            if (Cutoff(AppSettings.CallRetentionDays) is { } cc) b.Calls.RemoveAll(x => x.Date < cc);
            if (Cutoff(AppSettings.NotificationRetentionDays) is { } cn) b.Notifications.RemoveAll(x => !x.IsActive && x.FirstSeen < cn);
            if (b.Sms.Count + b.Calls.Count + b.Notifications.Count != before) { Save(key, b); any = true; }
        }
        if (any) Changed?.Invoke();
    }

    // MARK: 定時收集

    public void Start()
    {
        if (_timer is not null) return;
        _ui = DispatcherQueue.GetForCurrentThread();
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(15);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Prune();
    }

    private void Tick()
    {
        if (!AppSettings.PhoneDataEnabled) return;
        _tick++;
        // 通知每 15 秒；簡訊與通話記錄每分鐘
        bool full = _tick % 4 == 1;
        foreach (var d in DeviceManager.Shared.OnlineDevices.Where(d => d.Info is not null).ToList()) _ = SyncAsync(d, full);
        if (_tick % 240 == 0) Prune();
    }

    /// <summary>立刻同步一台裝置（UI 的「立即更新」也走這裡）</summary>
    /// <summary>測試用（--no-sync）：不去手機收集，只顯示已保存的資料</summary>
    public static bool TestNoSync { get; set; }

    public async Task SyncAsync(Device device, bool messagesAndCalls = true)
    {
        if (TestNoSync || device.Info is null) return;
        var key = device.Key;
        if (!_syncing.Add(key)) return;
        SyncStateChanged?.Invoke();
        try
        {
            var c = device.Client;
            List<PhoneSMS> newSms = []; List<PhoneCall> newCalls = [];
            if (messagesAndCalls)
            {
                var b0 = Load(key);
                long smsFrom = Math.Max(b0.Sms.Count > 0 ? b0.Sms.Max(x => x.Date).ToUnixTimeMilliseconds() : 0, Cutoff(AppSettings.SmsRetentionDays)?.ToUnixTimeMilliseconds() ?? 0);
                long callFrom = Math.Max(b0.Calls.Count > 0 ? b0.Calls.Max(x => x.Date).ToUnixTimeMilliseconds() : 0, Cutoff(AppSettings.CallRetentionDays)?.ToUnixTimeMilliseconds() ?? 0);
                try { newSms = PhoneDataParser.Sms(await c.ShellAsync(Query("content://sms", PhoneDataParser.SmsKeys, smsFrom), 90)); } catch { }
                try { newCalls = PhoneDataParser.Calls(await c.ShellAsync(Query("content://call_log/calls", PhoneDataParser.CallKeys, callFrom), 60)); } catch { }
            }
            List<PhoneDataParser.ActiveNotification>? active = null;
            try { active = PhoneDataParser.Notifications(await c.ShellAsync("dumpsys notification --noredact", 30)); } catch { }

            var b = Load(key);
            var haveSms = b.Sms.Select(x => x.Id).ToHashSet(); var haveCalls = b.Calls.Select(x => x.Id).ToHashSet();
            var addSms = newSms.Where(x => !haveSms.Contains(x.Id)).ToList(); var addCalls = newCalls.Where(x => !haveCalls.Contains(x.Id)).ToList();
            b.Sms.AddRange(addSms); b.Calls.AddRange(addCalls);
            bool changed = addSms.Count > 0 || addCalls.Count > 0;
            if (active is not null) changed |= Merge(active, b.Notifications);
            if (messagesAndCalls || active is not null) b.LastSync = DateTimeOffset.Now;
            Save(key, b);
            if (addSms.Count > 0 || addCalls.Count > 0) Prune();
            if (changed) Changed?.Invoke();
        }
        finally { _syncing.Remove(key); SyncStateChanged?.Invoke(); }
    }

    /// <summary>重新對照手機上目前的通知：不見的標成已清除、新出現的加入、內容改變的更新。回傳有沒有變動。</summary>
    private static bool Merge(List<PhoneDataParser.ActiveNotification> active, List<PhoneNotification> list)
    {
        bool changed = false;
        var now = DateTimeOffset.Now;
        var keys = active.Select(a => a.Key).ToHashSet();
        foreach (var n in list.Where(n => n.IsActive && !keys.Contains(n.Key))) { n.ClearedAt = now; changed = true; }
        foreach (var a in active)
        {
            var n = list.FirstOrDefault(x => x.IsActive && x.Key == a.Key);
            if (n is not null)
            {
                if (n.Title != a.Title || n.Text != a.Text || n.Ongoing != a.Ongoing) { n.Title = a.Title; n.Text = a.Text; n.Ongoing = a.Ongoing; changed = true; }
            }
            else
            {
                list.Add(new PhoneNotification { Id = $"{a.Key}#{now.ToUnixTimeMilliseconds()}", Key = a.Key, Package = a.Package, Title = a.Title, Text = a.Text, Ongoing = a.Ongoing, FirstSeen = now });
                changed = true;
            }
        }
        return changed;
    }

    private static string Query(string uri, string[] keys, long sinceMs)
    {
        var cmd = $"content query --uri {uri} --projection {string.Join(':', keys)}";
        if (sinceMs > 0) cmd += " --where " + Shell.Quote($"date>{sinceMs}");
        return cmd + " --sort 'date ASC'";
    }
}
