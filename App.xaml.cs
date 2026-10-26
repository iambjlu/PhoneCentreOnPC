using Microsoft.UI.Xaml;
using PhoneCentre.Services;
using PhoneCentre.Support;

namespace PhoneCentre;

public partial class App : Application
{
    public static MainWindow MainWindow { get; private set; } = null!;
    private static TrayIcon? _tray;
    private static TrayPopup? _popup;
    private static bool _quitting;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _testTimer;

    public static bool TrayVisible => _tray?.Visible == true;

    /// <summary>叫出主視窗（從系統匣或關閉後重新開啟）</summary>
    public static void ShowMainWindow()
    {
        MainWindow.AppWindow.Show();
        if (MainWindow.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        MainWindow.Activate();
    }

    /// <summary>結束 PhoneCentre：先讓虛擬顯示器上的 App 收尾</summary>
    public static async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tray?.Dispose();
        await WindowManager.ShutdownAsync();
        Environment.Exit(0);
    }

    public App()
    {
        Fonts.Install();   // 要在 XAML 第一次用到字型之前
        Log.Write("App 建構");
        try { InitializeComponent(); } catch (Exception e) { Log.Write("App.InitializeComponent 例外 " + e); throw; }
        Log.Write("App.InitializeComponent 完成");
        UnhandledException += (_, e) => { Log.Write("未處理的例外 " + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("AppDomain 例外 " + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Log.Write("OnLaunched");
        Fonts.Apply(this);
        if (Environment.GetCommandLineArgs().Contains("--fonttest")) { new FontTestWindow().Activate(); return; }
        MainWindow = new MainWindow();
        Log.Write("MainWindow 建立完成");
        MainWindow.Activate();
        _tray = new TrayIcon(MainWindow, MainWindow.Subclass);
        _popup = new TrayPopup();
        _tray.Clicked += () => MainWindow.DispatcherQueue.TryEnqueue(_popup.Toggle);
        void SyncTray() => _tray.SetVisible(Settings.AppSettings.Data.ShowTrayIcon);
        Settings.AppSettings.Changed += SyncTray;
        SyncTray();
        MainWindow.AppWindow.Closing += (_, e) =>
        {
            if (_quitting) return;
            if (TrayVisible) { e.Cancel = true; MainWindow.AppWindow.Hide(); }
            else { e.Cancel = true; Quit(); }
        };
        Log.Write("啟動設定監看");
        Settings.AppSettings.StartWatching();
        Log.Write("啟動快取管理");
        CacheManager.Shared.Start();
        Log.Write("啟動裝置管理");
        DeviceManager.Shared.Start();
        Log.Write("啟動手機狀態");
        DeviceStatusStore.Shared.Start();
        Log.Write("啟動手機資料");
        PhoneDataStore.Shared.Start();
        Log.Write("啟動完成");

        if (Environment.GetCommandLineArgs().Contains("--no-sync")) PhoneDataStore.TestNoSync = true;
        // 測試用：--enable-phonedata 開啟簡訊／通知／通話記錄收集；--tab=launchpad 等
        if (Environment.GetCommandLineArgs().Contains("--enable-phonedata")) Settings.AppSettings.PhoneDataEnabled = true;
        var tabArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--tab="))?["--tab=".Length..];
        if (tabArg is not null && Enum.TryParse<DeviceTab>(tabArg, true, out var tab)) MainWindow.SelectTab(tab);

        // 測試用：--tray 啟動後自動叫出系統匣視窗
        if (Environment.GetCommandLineArgs().Contains("--tray"))
        {
            _testTimer = MainWindow.DispatcherQueue.CreateTimer(); var t = _testTimer; t.Interval = TimeSpan.FromSeconds(6); t.IsRepeating = false;
            t.Tick += (_, _) => { MainWindow.AppWindow.Hide(); _popup!.Toggle(); }; t.Start();
        }
        // 測試用：--settings=<頁號> 直接開設定視窗
        var setArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--settings="))?["--settings=".Length..];
        if (setArg is not null && int.TryParse(setArg, out var page)) WindowManager.OpenSettings(page);

        // 測試用：--mirror 在第一台在線裝置出現後自動開鏡像視窗
        var cmdline = Environment.GetCommandLineArgs();
        var appArg = cmdline.FirstOrDefault(a => a.StartsWith("--app="))?["--app=".Length..];
        if (cmdline.Contains("--mirror") || cmdline.Contains("--desktop") || appArg is not null)
        {
            void Once()
            {
                if (DeviceManager.Shared.OnlineDevices.FirstOrDefault(d => d.Info is not null) is not { } d) return;
                DeviceManager.Shared.Changed -= Once;
                if (appArg is not null) WindowManager.OpenApp(d, appArg);
                else if (cmdline.Contains("--desktop")) WindowManager.OpenDesktop(d);
                else WindowManager.OpenMirror(d);
            }
            DeviceManager.Shared.Changed += Once;
        }
    }
}
