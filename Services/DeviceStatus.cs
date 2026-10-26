using System.Text.RegularExpressions;
using PhoneCentre.Adb;

namespace PhoneCentre.Services;

/// <summary>一個卡槽的資訊</summary>
public sealed record SimInfo
{
    public int Slot { get; set; } = 1;
    /// <summary>目前收到的電信（基地台資訊裡已註冊的網路名稱），不是 SIM 卡本身的業者</summary>
    public string Carrier { get; set; } = "";
    public string Tech { get; set; } = "";        // 例如「LTE」「5G」
    public int? Dbm { get; set; }
    public int Bars { get; set; }
    public bool InService { get; set; }
    public bool PowerOff { get; set; }            // 無線電關閉（飛航模式等）
    public bool EmergencyOnly { get; set; }       // 只能撥緊急電話
}

/// <summary>手機狀態：電池、網路（Wi-Fi 或行動網路世代）、主要 SIM 卡訊號格數（對應 DeviceStatus.swift）</summary>
public sealed record DeviceStatus
{
    public int BatteryLevel { get; set; } = -1;   // 0..100，-1 = 未知
    public bool Charging { get; set; }
    public bool Wifi { get; set; }
    /// <summary>行動網路世代，例如「4G」「5G」；沒有行動資料連線時為 null</summary>
    public string? CellularTech { get; set; }
    public bool HasSim { get; set; }
    public int SignalBars { get; set; }           // 0..4
    public bool InService { get; set; }
    public int WifiLevel { get; set; }            // 0..4
    public bool AutoRotate { get; set; } = true;
    public int UserRotation { get; set; }         // 0..3
    public bool ForceOrientation { get; set; }
    /// <summary>BatteryManager.BATTERY_STATUS_*：2 充電中、3 放電中、4 插著但沒在充、5 已充飽；-1 未知</summary>
    public int BatteryStatus { get; set; } = -1;
    public bool PluggedIn { get; set; }
    public string Carrier { get; set; } = "";
    public List<SimInfo> Sims { get; set; } = [];
    public string RadioTech { get; set; } = "";
    public int? SignalDbm { get; set; }
    public string WifiSSID { get; set; } = "";
    public string WifiIP { get; set; } = "";
    public int? BatteryCycles { get; set; }
    public int? BatteryMaxMah { get; set; }
    public int? BatteryDesignMah { get; set; }
    public string ManufactureDate { get; set; } = "";   // yyyyMMdd（三星的 RF 校正日期，約等於出廠日）

    public bool SameAs(DeviceStatus? o) => o is not null && System.Text.Json.JsonSerializer.Serialize(this) == System.Text.Json.JsonSerializer.Serialize(o);

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (BatteryLevel >= 0) parts.Add($"電池 {BatteryLevel}%" + (Charging ? "（充電中）" : ""));
            if (Wifi) parts.Add($"Wi-Fi {WifiLevel}/4");
            else if (CellularTech is { } t) parts.Add($"行動網路 {t}");
            else parts.Add("沒有網路");
            if (HasSim) parts.Add(InService ? $"訊號 {SignalBars}/4" : "沒有服務");
            return string.Join(" · ", parts);
        }
    }

    public const string Script = """
        echo '@@bat'; dumpsys battery | grep -E '^  (level|status|AC powered|USB powered|Wireless powered):'
        echo '@@net'; dumpsys connectivity | grep -E 'Active default network|NetworkAgentInfo\{network\{' | cut -c1-150
        echo '@@tel'; dumpsys telephony.registry | grep -E '^  Phone Id=|^    (mServiceState|mSignalStrength|mDataConnectionState|mCellInfo|mCellIdentity)=' | cut -c1-2000
        echo '@@sim'; getprop gsm.sim.state
        echo '@@wifi'; dumpsys wifi | grep -m1 -oE 'RSSI: -?[0-9]+'
        echo '@@wifiinfo'; dumpsys wifi | grep -m1 'mWifiInfo SSID' | cut -c1-200
        echo '@@ip'; ip -f inet addr show wlan0 2>/dev/null | grep -m1 inet
        echo '@@sys'; for f in cycle_count battery_cycle fg_cycle charge_full_design fg_fullcapnom charge_full; do echo "$f=$(cat /sys/class/power_supply/battery/$f 2>/dev/null)"; done
        echo '@@mfg'; getprop ril.rfcal_date
        echo '@@rot'; settings get system accelerometer_rotation; settings get system user_rotation
        echo '@@force'; wm get-ignore-orientation-request 2>/dev/null
        """;

    public static async Task<DeviceStatus?> FetchAsync(AdbClient c)
    {
        try { return Parse(await c.ShellAsync(Script, 10)); } catch { return null; }
    }

    private static int? ToInt(string s) => int.TryParse(s.Trim(), out var v) ? v : null;

    public static DeviceStatus Parse(string text)
    {
        var sections = new Dictionary<string, List<string>>();
        var current = "";
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (line.StartsWith("@@")) { current = line[2..]; continue; }
            if (!sections.TryGetValue(current, out var l)) sections[current] = l = [];
            l.Add(line);
        }
        List<string> Sec(string k) => sections.GetValueOrDefault(k) ?? [];
        var s = new DeviceStatus();

        // 電池
        foreach (var l in Sec("bat"))
        {
            var t = l.Trim();
            if (t.StartsWith("level:")) s.BatteryLevel = ToInt(t[6..]) ?? -1;
            if (t.StartsWith("status:")) s.BatteryStatus = ToInt(t[7..]) ?? -1;
            if (t.EndsWith("powered: true")) s.PluggedIn = true;
        }
        // 插著電不一定在充電（例如最佳化電池充電）：有狀態碼就只有「充電中」才算
        s.Charging = s.BatteryStatus >= 0 ? s.BatteryStatus == 2 : s.PluggedIn;

        // 目前預設網路：WIFI 或 MOBILE[LTE]
        var net = Sec("net");
        var activeLine = net.FirstOrDefault(l => l.Contains("Active default network"));
        var active = activeLine?.Split(':').Last().Trim();
        if (active is not null)
        {
            var agent = net.FirstOrDefault(l => l.Contains($"network{{{active}}}"));
            int ni = agent?.IndexOf("ni{", StringComparison.Ordinal) ?? -1;
            if (agent is not null && ni >= 0)
            {
                var info = new string(agent[(ni + 3)..].TakeWhile(ch => ch != ' ').ToArray());
                if (info.StartsWith("WIFI")) s.Wifi = true;
                else if (info.StartsWith("MOBILE"))
                {
                    int lb = info.IndexOf('[');
                    var rat = lb >= 0 ? new string(info[(lb + 1)..].TakeWhile(ch => ch != ']').ToArray()) : "";
                    s.CellularTech = Generation(rat);
                }
            }
        }

        var rssiLine = Sec("wifi").FirstOrDefault(l => l.StartsWith("RSSI:"));
        if (s.Wifi && rssiLine is not null && ToInt(rssiLine.Replace("RSSI:", "")) is int rssi)
            s.WifiLevel = Math.Max(0, Math.Min(4, (rssi + 100) * 4 / 45));   // 同 Android 的 calculateSignalLevel
        else if (s.Wifi) s.WifiLevel = 4;

        // Wi-Fi 名稱與 IP（grep 提早結束時 dumpsys 會多印一行 Broken pipe，所以要找含 SSID 的那一行）
        var ssidLine = Sec("wifiinfo").FirstOrDefault(l => l.Contains("SSID: \""));
        if (ssidLine is not null)
        {
            int i = ssidLine.IndexOf("SSID: \"", StringComparison.Ordinal) + 7;
            s.WifiSSID = new string(ssidLine[i..].TakeWhile(ch => ch != '"').ToArray());
        }
        var ipLine = Sec("ip").FirstOrDefault(l => l.Contains("inet "));
        if (ipLine is not null)
        {
            int i = ipLine.IndexOf("inet ", StringComparison.Ordinal) + 5;
            s.WifiIP = new string(ipLine[i..].TakeWhile(ch => ch != '/' && ch != ' ').ToArray());
        }

        // 電池循環次數與容量（sysfs；各家欄位不同）
        var sys = new Dictionary<string, int>();
        foreach (var l in Sec("sys"))
        {
            var kv = l.Split('=', 2);
            if (kv.Length == 2 && ToInt(kv[1]) is int v) sys[kv[0]] = v;
        }
        s.BatteryCycles = new[] { "cycle_count", "battery_cycle", "fg_cycle" }.Select(k => sys.GetValueOrDefault(k)).Where(v => v > 0).Cast<int?>().FirstOrDefault();
        static int? Mah(int? v) => v is > 0 ? (v > 100_000 ? v / 1000 : v) : null;
        s.BatteryDesignMah = Mah(sys.TryGetValue("charge_full_design", out var d) ? d : null);
        s.BatteryMaxMah = Mah(sys.TryGetValue("fg_fullcapnom", out var m) ? m : null) ?? Mah(sys.TryGetValue("charge_full", out var cf) ? cf : null);
        s.ManufactureDate = (Sec("mfg").FirstOrDefault() ?? "").Trim();
        s.ForceOrientation = Sec("force").Any(l => l.Contains("true"));
        var rot = Sec("rot");
        if (rot.Count >= 1) s.AutoRotate = rot[0].Trim() != "0";
        if (rot.Count >= 2) s.UserRotation = ToInt(rot[1]) ?? 0;

        // SIM 與訊號：有資料連線的那張算主要 SIM，否則取第一張
        var simStates = (Sec("sim").FirstOrDefault() ?? "").Split(',').Select(x => x.Trim()).ToList();
        s.HasSim = simStates.Any(x => x != "ABSENT" && x.Length > 0 && x != "UNKNOWN");
        var phones = new List<List<string>>();
        foreach (var l in Sec("tel"))
        {
            if (l.StartsWith("  Phone Id=")) phones.Add([]);
            else if (phones.Count > 0) phones[^1].Add(l);
        }
        var parsed = phones.Select(ParsePhone).ToList();
        for (int i = 0; i < parsed.Count; i++)
        {
            parsed[i].Slot = i + 1;
            if (simStates.Count == parsed.Count && (simStates[i] is "ABSENT" or "UNKNOWN" or "")) continue;
            s.Sims.Add(parsed[i]);
        }
        int primary = phones.FindIndex(p => p.Any(l => l.Contains("mDataConnectionState=2")));
        if (primary < 0 && phones.Count > 0) primary = 0;
        if (primary >= 0)
        {
            var p = parsed[primary];
            s.InService = p.InService; s.RadioTech = p.Tech; s.Carrier = p.Carrier; s.SignalDbm = p.Dbm; s.SignalBars = p.Bars;
        }
        return s;
    }

    /// <summary>解析一個卡槽（telephony.registry 的一個 Phone Id 區塊）</summary>
    private static SimInfo ParsePhone(List<string> lines)
    {
        var p = new SimInfo();
        var svc = lines.FirstOrDefault(l => l.Contains("mServiceState="));
        if (svc is not null)
        {
            p.InService = svc.Contains("mVoiceRegState=0") || svc.Contains("mDataRegState=0");
            p.EmergencyOnly = svc.Contains("mIsEmergencyOnly=true");
            p.PowerOff = svc.Contains("mVoiceRegState=3");
            // 「getRilDataRadioTechnology=14(LTE)」→ LTE；NR 顯示 5G
            foreach (var key in new[] { "getRilDataRadioTechnology=", "getRilVoiceRadioTechnology=" })
            {
                int k = svc.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) continue;
                int o = svc.IndexOf('(', k + key.Length);
                if (o < 0) continue;
                var name = new string(svc[(o + 1)..].TakeWhile(ch => ch != ')').ToArray());
                if (name.Length > 0 && name != "Unknown") { p.Tech = name.StartsWith("NR") ? "5G" : name; break; }
            }
        }
        // 已註冊的基地台可能不只一個，有的是無效資料；名稱是空的或全是數字就跳過
        static string? Alpha(string s)
        {
            int a = s.IndexOf("mAlphaLong=", StringComparison.Ordinal);
            if (a < 0) return null;
            var name = new string(s[(a + 11)..].TakeWhile(ch => ch != ' ' && ch != '}').ToArray());
            return name.Length == 0 || name.All(char.IsDigit) ? null : name;
        }
        var cell = lines.FirstOrDefault(l => l.Contains("mCellInfo="));
        if (cell is not null)
        {
            var rest = cell;
            while (p.Carrier.Length == 0)
            {
                int reg = rest.IndexOf("mRegistered=YES", StringComparison.Ordinal);
                if (reg < 0) break;
                rest = rest[(reg + 15)..];
                int end = rest.IndexOf("mRegistered=", StringComparison.Ordinal);
                var seg = end < 0 ? rest : rest[..end];
                if (Alpha(seg) is { } name) p.Carrier = name;
            }
        }
        if (p.Carrier.Length == 0)
        {
            var id = lines.FirstOrDefault(l => l.Contains("mCellIdentity="));
            if (id is not null && Alpha(id) is { } name) p.Carrier = name;
        }
        var sig = lines.FirstOrDefault(l => l.Contains("mSignalStrength="));
        if (sig is not null)
        {
            foreach (var key in new[] { "rsrp=", "ssRsrp=", "rssi=" })
            {
                int k = sig.IndexOf(key, StringComparison.Ordinal);
                if (k < 0) continue;
                var num = new string(sig[(k + key.Length)..].TakeWhile(ch => ch == '-' || char.IsDigit(ch)).ToArray());
                if (int.TryParse(num, out var n) && n < 0 && n > -200) { p.Dbm = n; break; }
            }
            // 三星的狀態列用 SignalBarInfo 的格數（例如 lteLevel=4），沒有才用標準的 level=
            int sb = sig.IndexOf("SignalBarInfo{", StringComparison.Ordinal);
            int lv = sb >= 0 ? sig.IndexOf("Level=", sb, StringComparison.Ordinal) : -1;
            if (lv >= 0) p.Bars = Math.Min(4, int.TryParse(new string(sig[(lv + 6)..].TakeWhile(char.IsDigit).ToArray()), out var b) ? b : 0);
            else
            {
                int l2 = sig.IndexOf("level=", StringComparison.Ordinal);
                if (l2 >= 0) p.Bars = Math.Min(4, int.TryParse(new string(sig[(l2 + 6)..].TakeWhile(char.IsDigit).ToArray()), out var b) ? b : 0);
            }
        }
        return p;
    }

    private static string Generation(string rat) => rat.ToUpperInvariant() switch
    {
        "NR" or "NR_NSA" => "5G",
        "LTE" or "LTE_CA" or "IWLAN" => "4G",
        "HSPAP" or "HSPA" or "HSDPA" or "HSUPA" or "UMTS" or "TD_SCDMA" => "3G",
        "EDGE" or "GPRS" or "GSM" => "2G",
        "" => "4G",
        _ => rat,
    };
}

/// <summary>主視窗用的手機狀態（側邊欄每台裝置的狀態圖、總覽）：每 10 秒更新所有在線裝置</summary>
public sealed class DeviceStatusStore
{
    public static DeviceStatusStore Shared { get; } = new();
    private readonly Dictionary<string, DeviceStatus> _statuses = new();
    private readonly HashSet<string> _inFlight = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private Microsoft.UI.Dispatching.DispatcherQueue? _queue;

    public event Action? Changed;

    public void Start()
    {
        if (_timer is not null) return;
        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(10);
        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
        RefreshAll();
    }

    public DeviceStatus? StatusFor(Device d) => _statuses.GetValueOrDefault(d.Id);

    public void RefreshAll()
    {
        var online = DeviceManager.Shared.OnlineDevices;
        var ids = online.Select(d => d.Id).ToHashSet();
        foreach (var k in _statuses.Keys.Where(k => !ids.Contains(k)).ToList()) _statuses.Remove(k);
        foreach (var d in online.Where(d => d.Info is not null)) Refresh(d);
    }

    public void Refresh(Device d)
    {
        if (!_inFlight.Add(d.Id)) return;
        _ = Task.Run(async () =>
        {
            var s = await DeviceStatus.FetchAsync(d.Client);
            return s;
        }).ContinueWith(t => _queue?.TryEnqueue(() =>
        {
            _inFlight.Remove(d.Id);
            var s = t.Result;
            if (s is not null && !s.SameAs(_statuses.GetValueOrDefault(d.Id))) { _statuses[d.Id] = s; Changed?.Invoke(); }
        }));
    }
}
