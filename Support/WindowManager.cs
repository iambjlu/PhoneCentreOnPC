using PhoneCentre.Adb;
using PhoneCentre.Mirror;
using PhoneCentre.Services;
using PhoneCentre.Settings;

namespace PhoneCentre;

/// <summary>開啟鏡像相關視窗（對應 WindowManager.swift）：主螢幕鏡像、獨立 App 視窗（虛擬顯示器）、桌面模式</summary>
public static class WindowManager
{
    private static readonly Dictionary<string, MirrorWindow> Open = new();
    private static readonly Dictionary<string, LocationSimulatorWindow> Locations = new();
    private static SettingsWindow? _settings;

    public static int OpenCount => Open.Count;

    /// <summary>結束程式前：虛擬顯示器上的 App 先收尾（最多等 6 秒），再停掉所有鏡像</summary>
    public static async Task ShutdownAsync()
    {
        var models = Open.Values.Select(w => w.Model).ToList();
        try { await Task.WhenAny(Task.WhenAll(models.Select(m => m.PrepareForQuitAsync())), Task.Delay(6000)); } catch { }
        foreach (var m in models) { try { m.Stop(); } catch { } }
    }

    private static string Key(Device d, string? pkg, bool desktop = false) => $"{d.Id}#{pkg ?? (desktop ? "@desktop" : "@main")}";

    /// <summary>主螢幕鏡像</summary>
    public static void OpenMirror(Device device) => Show(device, null, false);

    /// <summary>在獨立視窗（虛擬顯示器）執行 App。at：視窗左上角放在這個螢幕位置（從 App 列拖出來放開的地方）</summary>
    public static void OpenApp(Device device, string package, (double X, double Y)? at = null)
    {
        var catalog = DeviceManager.Shared.Catalog(device);
        Show(device, package, false, catalog.Apps.Count == 0 ? null : catalog.Label(package), at);
    }

    /// <summary>桌面模式（虛擬顯示器＋系統裝飾）</summary>
    public static void OpenDesktop(Device device) => Show(device, null, true);

    /// <summary>依啟動台的「開啟方式」啟動 App（對應 WindowManager.launch）</summary>
    public static void Launch(Device device, string package)
    {
        if (AppSettings.LaunchTarget == "SeparateWindow") OpenApp(device, package);
        else LaunchOnMainScreen(device, package);
    }

    /// <summary>在手機主螢幕開啟 App：有鏡像視窗就透過它送，沒有就用 adb 開並順便開鏡像視窗</summary>
    public static void LaunchOnMainScreen(Device device, string package)
    {
        if (Open.TryGetValue(Key(device, null), out var w)) { w.Model.Launch(package); w.Activate(); return; }
        _ = DeviceManager.Shared.Catalog(device).LaunchAsync(package);
        OpenMirror(device);
    }

    private static void Show(Device device, string? package, bool desktop, string? label = null, (double X, double Y)? at = null)
    {
        var k = Key(device, package, desktop);
        if (Open.TryGetValue(k, out var existing)) { existing.Activate(); return; }
        var model = new MirrorModel(device, package, desktop) { AppLabel = label };
        var w = new MirrorWindow(model, at);
        w.OnClosed = () => Open.Remove(k);
        Open[k] = w;
        w.Activate();
    }

    public static bool IsOpen(Device device) => Open.Keys.Any(k => k.StartsWith(device.Id + "#"));

    public static void OpenLocation(Device device)
    {
        if (Locations.TryGetValue(device.Id, out var w)) { w.Activate(); return; }
        w = new LocationSimulatorWindow(device);
        w.Closed += (_, _) => Locations.Remove(device.Id);
        Locations[device.Id] = w;
        w.Activate();
    }

    public static void OpenSettings(int page = 0)
    {
        if (_settings is not null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(page);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Activate();
    }
}
