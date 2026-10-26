using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;

namespace PhoneCentre;

/// <summary>開啟鏡像相關視窗（對應 WindowManager.swift；獨立 App 視窗與桌面模式之後再做）</summary>
public static class WindowManager
{
    private static readonly Dictionary<string, MirrorWindow> Mirrors = new();

    public static void OpenMirror(Device device)
    {
        if (Mirrors.TryGetValue(device.Id, out var w)) { w.Activate(); return; }
        w = new MirrorWindow(device);
        Mirrors[device.Id] = w;
        w.Closed += (_, _) => Mirrors.Remove(device.Id);
        w.Activate();
    }

    private static SettingsWindow? _settings;

    public static void OpenSettings(int page = 0)
    {
        if (_settings is not null) { _settings.Activate(); return; }
        _settings = new SettingsWindow(page);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Activate();
    }

    /// <summary>依啟動台的「開啟方式」啟動 App（對應 WindowManager.launch）</summary>
    public static void Launch(Device device, string package)
    {
        if (AppSettings.LaunchTarget == "SeparateWindow") OpenApp(device, package);
        else LaunchOnMainScreen(device, package);
    }

    /// <summary>在手機主螢幕開啟 App：有鏡像視窗就透過它送，沒有就用 adb 開並順便開鏡像視窗</summary>
    public static void LaunchOnMainScreen(Device device, string package)
    {
        if (Mirrors.TryGetValue(device.Id, out var w)) { w.Launch(package); w.Activate(); return; }
        _ = DeviceManager.Shared.Catalog(device).LaunchAsync(package);
        OpenMirror(device);
    }

    /// <summary>目前還沒有獨立視窗（虛擬顯示器）：先在主螢幕開啟</summary>
    public static void OpenApp(Device device, string package) => LaunchOnMainScreen(device, package);
}
