using System.Text.Json;
using PhoneCentre.Support;

namespace PhoneCentre.Settings;

/// <summary>設定（%LOCALAPPDATA%\PhoneCentre\settings.json）。目前只有協定層與總覽需要的欄位，其餘之後補。</summary>
public static class AppSettings
{
    public sealed class SettingsData
    {
        public bool KeepAlive { get; set; } = true;
        public string CustomAdbPath { get; set; } = "";
        /// <summary>啟動台點 App 的開啟方式：SeparateWindow（獨立視窗）／MainScreen（手機主螢幕）</summary>
        public string LaunchTarget { get; set; } = "SeparateWindow";
        /// <summary>是否定時收集簡訊、通知、通話記錄（個人隱私資料，只存在這台電腦；預設關閉）</summary>
        public bool PhoneDataEnabled { get; set; }
        /// <summary>各類資料的保存天數，0＝永久</summary>
        public int SmsRetentionDays { get; set; } = 30;
        public int CallRetentionDays { get; set; } = 30;
        public int NotificationRetentionDays { get; set; } = 30;
        /// <summary>依裝置鍵釘選的 App（喜好項目）</summary>
        public Dictionary<string, List<string>> Pinned { get; set; } = new();
    }

    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Assets.SupportDir, "settings.json");
    private static SettingsData _data = Load();

    public static event Action? Changed;

    private static SettingsData Load()
    {
        try { return JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    private static void Save()
    {
        try { lock (Gate) File.WriteAllText(FilePath, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
        Changed?.Invoke();
    }

    public static bool KeepAlive => _data.KeepAlive;

    public static bool PhoneDataEnabled
    {
        get => _data.PhoneDataEnabled;
        set { _data.PhoneDataEnabled = value; Save(); }
    }

    public static int SmsRetentionDays { get => _data.SmsRetentionDays; set { _data.SmsRetentionDays = value; Save(); } }
    public static int CallRetentionDays { get => _data.CallRetentionDays; set { _data.CallRetentionDays = value; Save(); } }
    public static int NotificationRetentionDays { get => _data.NotificationRetentionDays; set { _data.NotificationRetentionDays = value; Save(); } }

    public static string LaunchTarget
    {
        get => _data.LaunchTarget;
        set { _data.LaunchTarget = value; Save(); }
    }

    public static string CustomAdbPath
    {
        get => _data.CustomAdbPath;
        set { _data.CustomAdbPath = value; Save(); }
    }

    public static IReadOnlyList<string> Pinned(string deviceKey) =>
        _data.Pinned.TryGetValue(deviceKey, out var l) ? l : [];

    public static bool IsPinned(string package, string deviceKey) => Pinned(deviceKey).Contains(package);

    public static void TogglePin(string package, string deviceKey)
    {
        if (!_data.Pinned.TryGetValue(deviceKey, out var l)) _data.Pinned[deviceKey] = l = [];
        if (!l.Remove(package)) l.Add(package);
        Save();
    }
}
