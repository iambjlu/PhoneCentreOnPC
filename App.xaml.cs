using Microsoft.UI.Xaml;
using PhoneCentre.Services;
using PhoneCentre.Support;

namespace PhoneCentre;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;

    public App()
    {
        Log.Write("App 建構");
        try { InitializeComponent(); } catch (Exception e) { Log.Write("App.InitializeComponent 例外 " + e); throw; }
        Log.Write("App.InitializeComponent 完成");
        UnhandledException += (_, e) => { Log.Write("未處理的例外 " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("AppDomain 例外 " + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write("OnLaunched");
        MainWindow = new MainWindow();
        Log.Write("MainWindow 建立完成");
        MainWindow.Activate();
        DeviceManager.Shared.Start();
        DeviceStatusStore.Shared.Start();

        // 測試用：--tab=launchpad 等
        var tabArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tab="))?["--tab=".Length..];
        if (tabArg is not null && Enum.TryParse<DeviceTab>(tabArg, true, out var tab)) MainWindow.SelectTab(tab);

        // 測試用：--mirror 在第一台在線裝置出現後自動開鏡像視窗
        if (Environment.GetCommandLineArgs().Contains("--mirror"))
        {
            void Once()
            {
                if (DeviceManager.Shared.OnlineDevices.FirstOrDefault() is not { } d) return;
                DeviceManager.Shared.Changed -= Once;
                WindowManager.OpenMirror(d);
            }
            DeviceManager.Shared.Changed += Once;
        }
    }
}
