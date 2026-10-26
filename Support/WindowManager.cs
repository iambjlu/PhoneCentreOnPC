using PhoneCentre.Adb;
using PhoneCentre.Services;

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

    /// <summary>在手機主螢幕開啟 App（有鏡像視窗就透過它送，沒有就直接用 adb）</summary>
    public static void Launch(Device device, string package)
    {
        if (Mirrors.TryGetValue(device.Id, out var w)) { w.Launch(package); return; }
        _ = DeviceManager.Shared.Catalog(device).LaunchAsync(package);
    }

    /// <summary>目前還沒有獨立視窗（虛擬顯示器）：先在主螢幕開啟</summary>
    public static void OpenApp(Device device, string package) => Launch(device, package);
}
