using System.Text.Json;
using Microsoft.UI.Dispatching;
using PhoneCentre.Mirror;
using PhoneCentre.Support;

namespace PhoneCentre.Settings;

/// <summary>鍵盤輸入模式：Physical＝依實體按鍵位置送 Android keycode，不受輸入法影響；Ime＝用 Windows 輸入法組字，送出確定的文字</summary>
public enum KeyboardMode { Physical, Ime }

/// <summary>快取清除時機</summary>
public enum CacheClearPolicy { OnDisconnect, AfterDays, Never }

/// <summary>工作列顯示方式：整個程式共用一個圖示，或每個視窗各自一個</summary>
public enum TaskbarMode { Shared, Separate }

/// <summary>記住的無線 ADB 端點</summary>
public sealed class SavedEndpoint
{
    public string Address { get; set; } = "";          // host:port
    public string? SerialNo { get; set; }              // ro.serialno，用來在 port 變動時透過 mDNS 找回
    public string? Name { get; set; }
    /// <summary>斷線時自動重新連線（null＝舊設定檔，視為開啟）</summary>
    public bool? AutoReconnect { get; set; }
    public bool Auto => AutoReconnect ?? true;
}

/// <summary>遠端 PhoneCentre 主機</summary>
public sealed class RemoteHost
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 47500;
    public bool AutoConnect { get; set; } = true;
    public string SecretKey => $"remote.{Id}";
}

/// <summary>所有可同步的設定（存在本機或使用者指定資料夾的 settings.json）。讀檔時缺少的欄位用預設值，舊設定檔也能讀。</summary>
public sealed class SettingsData
{
    public bool KeepAlive { get; set; } = true;
    public bool RememberWireless { get; set; } = true;
    public TaskbarMode MirrorTaskbarMode { get; set; } = TaskbarMode.Shared;
    public TaskbarMode AppWindowTaskbarMode { get; set; } = TaskbarMode.Separate;
    /// <summary>啟動台點 App 的開啟方式：SeparateWindow（獨立視窗）／MainScreen（手機主螢幕）</summary>
    public string LaunchTarget { get; set; } = "SeparateWindow";
    public KeyboardMode KeyboardMode { get; set; } = KeyboardMode.Ime;
    public VideoOptions Video { get; set; } = new();
    public Dictionary<string, VideoOptions> PerDeviceVideo { get; set; } = new();
    public bool AudioEnabled { get; set; } = true;
    public bool MuteDeviceWhenForwarding { get; set; } = true;
    public bool ClipboardSync { get; set; } = true;
    public bool TurnScreenOffWhileMirroring { get; set; }
    public bool DesktopModeForAppWindows { get; set; }
    public bool ShowTrayIcon { get; set; } = true;
    /// <summary>遠端 PhoneCentre（Beta）總開關：關閉時不連任何遠端主機、也不開分享，介面上相關項目全部隱藏</summary>
    public bool RemoteEnabled { get; set; } = true;
    public bool ShareEnabled { get; set; }
    public int SharePort { get; set; } = 47500;
    public List<SavedEndpoint> SavedEndpoints { get; set; } = [];
    public List<RemoteHost> RemoteHosts { get; set; } = [];
    public int CacheLimitMB { get; set; } = 200;
    public CacheClearPolicy CacheClearPolicy { get; set; } = CacheClearPolicy.AfterDays;
    public int CacheClearDays { get; set; } = 7;
    public bool OfferHelloUnlock { get; set; } = true;
    /// <summary>透過 adb 收集並保存簡訊、通知、通話記錄（個人隱私資料，預設關閉）</summary>
    public bool PhoneDataEnabled { get; set; }
    /// <summary>各類資料保存幾天（0 = 永久）</summary>
    public int SmsRetentionDays { get; set; } = 30;
    public int CallRetentionDays { get; set; } = 30;
    public int NotificationRetentionDays { get; set; } = 30;
    /// <summary>每台裝置（<see cref="Adb.Device.Key"/>）釘選的 App 套件名稱，依釘選順序</summary>
    public Dictionary<string, List<string>> Pinned { get; set; } = new();
    /// <summary>各視窗上次的位置與大小：[左、上（像素）、客戶區寬、高（DIP）]，鍵是「Mirror.裝置.主螢幕／App／桌面」</summary>
    public Dictionary<string, double[]> WindowFrames { get; set; } = new();
    /// <summary>密碼類資料也寫進同步資料夾（明碼）</summary>
    public bool SyncSecrets { get; set; }
    public Dictionary<string, string> Secrets { get; set; } = new();
}

/// <summary>
/// 全域設定（對應 AppSettings.swift）。預設存在 %LOCALAPPDATA%\PhoneCentre\settings.json；可改選一個資料夾
/// （例如 OneDrive），設定寫進該資料夾的 settings.json，每 3 秒偵測其他電腦改過的檔案並重新載入。
/// 資料夾路徑與自訂 adb 路徑只存在本機（local.json）。密碼存在 <see cref="SecretStore"/>。
/// </summary>
public static class AppSettings
{
    private sealed class LocalData
    {
        public string CustomAdbPath { get; set; } = "";
        public string? SettingsFolder { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();
    private static LocalData _local = LoadLocal();
    private static SettingsData _data = LoadData() ?? new();
    private static DateTime _lastFileTime;
    private static string? _lastRaw;
    private static DispatcherQueueTimer? _timer;

    /// <summary>設定有變動（本機改的或從同步資料夾讀進來的）</summary>
    public static event Action? Changed;

    public static SettingsData Data => _data;

    private static string LocalFile => Path.Combine(Assets.SupportDir, "local.json");
    private static string DefaultFile => Path.Combine(Assets.SupportDir, "settings.json");
    public static string? SettingsFolder => _local.SettingsFolder;
    private static string SettingsFile => _local.SettingsFolder is { Length: > 0 } f ? Path.Combine(f, "settings.json") : DefaultFile;

    private static LocalData LoadLocal()
    {
        try { return JsonSerializer.Deserialize<LocalData>(File.ReadAllText(LocalFile)) ?? new(); } catch { return new(); }
    }
    private static void SaveLocal()
    {
        try { File.WriteAllText(LocalFile, JsonSerializer.Serialize(_local, Json)); } catch { }
    }

    private static SettingsData? Parse(string raw)
    {
        try { return JsonSerializer.Deserialize<SettingsData>(raw, Json); } catch { return null; }
    }

    private static SettingsData? LoadData()
    {
        foreach (var f in new[] { SettingsFile, DefaultFile }.Distinct())
        {
            try
            {
                if (!File.Exists(f)) continue;
                var raw = File.ReadAllText(f);
                _lastFileTime = File.GetLastWriteTimeUtc(f); _lastRaw = raw;
                if (Parse(raw) is { } d) return d;
            }
            catch { }
        }
        return null;
    }

    /// <summary>啟動同步資料夾的檔案監看（要在 UI 執行緒呼叫一次）</summary>
    public static void StartWatching()
    {
        if (_timer is not null) return;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => CheckFile();
        _timer.Start();
    }

    /// <summary>偵測其他電腦（例如透過 OneDrive）改了設定檔</summary>
    private static void CheckFile()
    {
        try
        {
            var f = SettingsFile;
            if (!File.Exists(f)) return;
            var t = File.GetLastWriteTimeUtc(f);
            if (t == _lastFileTime) return;
            _lastFileTime = t;
            var raw = File.ReadAllText(f);
            if (raw == _lastRaw || Parse(raw) is not { } n) return;
            _lastRaw = raw;
            // 對方沒同步密碼時保留本機的
            if (!n.SyncSecrets) n.Secrets = _data.Secrets;
            _data = n;
            Changed?.Invoke();
        }
        catch { }
    }

    /// <summary>修改設定：在回呼裡改 <see cref="Data"/>，結束後自動存檔並通知</summary>
    public static void Update(Action<SettingsData> change)
    {
        change(_data);
        Save();
    }

    public static void Save()
    {
        lock (Gate)
        {
            try
            {
                var copy = JsonSerializer.Deserialize<SettingsData>(JsonSerializer.Serialize(_data, Json), Json)!;
                if (!copy.SyncSecrets) copy.Secrets = new();
                var raw = JsonSerializer.Serialize(copy, Json);
                var f = SettingsFile;
                Directory.CreateDirectory(Path.GetDirectoryName(f)!);
                var tmp = f + ".tmp";
                File.WriteAllText(tmp, raw);
                File.Move(tmp, f, true);
                _lastRaw = raw; _lastFileTime = File.GetLastWriteTimeUtc(f);
            }
            catch (Exception e) { Log.Write("保存設定失敗 " + e.Message); }
        }
        Changed?.Invoke();
    }

    /// <summary>改變設定檔位置；會把目前設定寫到新位置（若新位置已有設定檔則改為讀取它）</summary>
    public static void SetSettingsFolder(string? folder)
    {
        _local.SettingsFolder = string.IsNullOrEmpty(folder) ? null : folder;
        SaveLocal();
        try
        {
            var f = SettingsFile;
            if (_local.SettingsFolder is not null && File.Exists(f) && Parse(File.ReadAllText(f)) is { } existing)
            {
                _data = existing; _lastRaw = File.ReadAllText(f); _lastFileTime = File.GetLastWriteTimeUtc(f);
                Changed?.Invoke();
                return;
            }
        }
        catch { }
        Save();
    }

    /// <summary>OneDrive 資料夾（沒有就回傳 null）</summary>
    public static string? OneDrivePath =>
        Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } p && Directory.Exists(p) ? p : null;

    // MARK: 本機專用

    public static string CustomAdbPath
    {
        get => _local.CustomAdbPath;
        set { _local.CustomAdbPath = value; SaveLocal(); }
    }

    // MARK: 常用欄位的捷徑（既有程式碼用）

    public static bool KeepAlive => _data.KeepAlive;
    public static string LaunchTarget { get => _data.LaunchTarget; set { _data.LaunchTarget = value; Save(); } }
    public static bool PhoneDataEnabled { get => _data.PhoneDataEnabled; set { _data.PhoneDataEnabled = value; Save(); } }
    public static int SmsRetentionDays { get => _data.SmsRetentionDays; set { _data.SmsRetentionDays = value; Save(); } }
    public static int CallRetentionDays { get => _data.CallRetentionDays; set { _data.CallRetentionDays = value; Save(); } }
    public static int NotificationRetentionDays { get => _data.NotificationRetentionDays; set { _data.NotificationRetentionDays = value; Save(); } }

    // MARK: 釘選 App

    public static IReadOnlyList<string> Pinned(string deviceKey) => _data.Pinned.TryGetValue(deviceKey, out var l) ? l : [];
    public static bool IsPinned(string package, string deviceKey) => Pinned(deviceKey).Contains(package);

    public static void TogglePin(string package, string deviceKey)
    {
        if (!_data.Pinned.TryGetValue(deviceKey, out var l)) _data.Pinned[deviceKey] = l = [];
        if (!l.Remove(package)) l.Add(package);
        Save();
    }

    // MARK: 影像設定

    public static VideoOptions VideoOptionsFor(string deviceKey) => _data.PerDeviceVideo.TryGetValue(deviceKey, out var o) ? o : _data.Video;
    public static void SetVideoOptions(VideoOptions o, string deviceKey) { _data.PerDeviceVideo[deviceKey] = o; Save(); }

    // MARK: 密碼（DPAPI；開啟同步時也寫入設定檔）

    public static string? Secret(string key)
    {
        if (SecretStore.Get(key) is { Length: > 0 } v) return v;
        if (_data.Secrets.TryGetValue(key, out var s) && s.Length > 0) { SecretStore.Set(key, s); return s; }   // 從同步資料夾取得後存回本機
        return null;
    }

    public static void SetSecret(string key, string? value)
    {
        SecretStore.Set(key, value);
        if (!string.IsNullOrEmpty(value)) _data.Secrets[key] = value; else _data.Secrets.Remove(key);
        Save();
    }

    public static string SharePassword { get => Secret("sharePassword") ?? ""; set => SetSecret("sharePassword", value); }
}
