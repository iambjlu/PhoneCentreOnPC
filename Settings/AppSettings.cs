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
