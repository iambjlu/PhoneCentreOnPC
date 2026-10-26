using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhoneCentre.Adb;
using PhoneCentre.Mirror;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.Storage.Pickers;

namespace PhoneCentre;

/// <summary>設定視窗（對應 SettingsView.swift）：一般、連線、鏡像、遠端 (Beta)、儲存</summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] PageTitles = ["一般", "連線", "鏡像", "儲存"];
    private readonly ContentControl _page = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private int _index;

    public SettingsWindow(int page = 0)
    {
        _index = page;
        Title = "設定";
        PhoneCentre.Support.WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(700, 780));
        _ = new WindowSubclass(this) { MinSize = () => (640, 520) };

        var tabs = new SegmentedBar(PageTitles) { Selected = page, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 8) };
        tabs.SelectionChanged += i => { _index = i; Show(); };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(tabs);
        Grid.SetRow(_page, 1); root.Children.Add(_page);
        Content = root;
        Show();
    }

    private void Show()
    {
        FrameworkElement body = _index switch { 0 => General(), 1 => Connection(), 2 => MirrorPage(), _ => Storage() };
        _page.Content = new ScrollViewer
        {
            Content = new StackPanel { Spacing = 20, Padding = new Thickness(24, 8, 24, 24), MaxWidth = 720, Children = { body } },
            HorizontalScrollMode = ScrollMode.Disabled,
        };
    }

    private static StackPanel Stack(params FrameworkElement[] items)
    {
        var p = new StackPanel { Spacing = 20 };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    private static SettingsData D => AppSettings.Data;
    private static void Set(Action<SettingsData> a) => AppSettings.Update(a);

    // MARK: 一般

    private FrameworkElement General() => Stack(
        Form.Section("系統匣", Form.Toggle("在系統匣顯示圖示", D.ShowTrayIcon, v => Set(d => d.ShowTrayIcon = v))),
        Form.Section("工作列",
            Form.Combo("手機鏡像視窗", [TaskbarMode.Shared, TaskbarMode.Separate], TaskbarTitle, D.MirrorTaskbarMode, v => Set(d => d.MirrorTaskbarMode = v)),
            Form.Combo("獨立視窗的 App", [TaskbarMode.Shared, TaskbarMode.Separate], TaskbarTitle, D.AppWindowTaskbarMode, v => Set(d => d.AppWindowTaskbarMode = v)),
            Form.Note("「每個視窗各自一個」會讓每個視窗在工作列上有自己的按鈕，顯示手機名稱或 App 圖示。")),
        Form.Section("啟動台",
            Form.Combo("點 App 時", ["MainScreen", "SeparateWindow"], t => t == "MainScreen" ? "在手機主螢幕開啟" : "在獨立視窗開啟", D.LaunchTarget, v => Set(d => d.LaunchTarget = v))));

    private static string TaskbarTitle(TaskbarMode m) => m == TaskbarMode.Shared ? "整個程式共用一個" : "每個視窗各自一個";

    // MARK: 連線

    private FrameworkElement Connection()
    {
        var saved = D.SavedEndpoints.Count == 0
            ? [(FrameworkElement)Form.Note("沒有")]
            : D.SavedEndpoints.Select(ep =>
            {
                var left = new StackPanel { Children = { Ui.Ui.Text(ep.Name ?? ep.Address, 14), Ui.Ui.Text(ep.Address, 12, true) } };
                var forget = Form.Btn("忘記", () => { DeviceManager.Shared.Forget(ep); Show(); });
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(left); Grid.SetColumn(forget, 1); g.Children.Add(forget);
                return (FrameworkElement)g;
            }).ToArray();

        var msg = Ui.Ui.Text("", 12, true);
        var path = new TextBox { Text = AppSettings.CustomAdbPath, PlaceholderText = "自訂 adb 路徑（留白自動尋找）", MinWidth = 340 };
        path.LostFocus += (_, _) => AppSettings.CustomAdbPath = path.Text.Trim();
        var install = Form.Btn("下載／更新 Google Platform-Tools", async () =>
        {
            try { await AdbLocator.InstallAsync(m => msg.Text = m); await DeviceManager.Shared.RefreshAsync(); Show(); }
            catch (Exception e) { msg.Text = e.Message; }
        });
        return Stack(
            Form.Section("自動維持連線",
                Form.Toggle("adb server 被關掉時自動重新啟動，並重連無線裝置", D.KeepAlive, v => Set(d => d.KeepAlive = v)),
                Form.Toggle("記住連線過的無線裝置", D.RememberWireless, v => Set(d => d.RememberWireless = v))),
            Form.Section("記住的無線裝置", saved),
            Form.Section("adb",
                Form.Value("目前使用", AdbLocator.Find() ?? "找不到"), path,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { install, msg } }));
    }

    // MARK: 鏡像

    private FrameworkElement MirrorPage()
    {
        var bitrate = new Slider { Minimum = 0.5, Maximum = 40, StepFrequency = 0.5, Value = D.Video.BitRateMbps, Width = 240 };
        var bitrateText = Ui.Ui.Text($"{D.Video.BitRateMbps:0.0} Mbps", 13, true); bitrateText.Width = 80; bitrateText.TextAlignment = TextAlignment.Right;
        bitrate.ValueChanged += (_, e) => { bitrateText.Text = $"{e.NewValue:0.0} Mbps"; Set(d => d.Video.BitRateMbps = e.NewValue); };
        var bitrateRow = Form.Row("Bitrate", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { bitrate, bitrateText } });
        return Stack(
            Form.Section("預設畫質（每台手機可在鏡像視窗個別調整）",
                Form.Combo("FPS", [15, 24, 30, 45, 60, 90, 120], v => v.ToString(), D.Video.MaxFps, v => Set(d => d.Video.MaxFps = v)),
                bitrateRow,
                Form.Combo("最大解析度", [0, 2560, 1920, 1600, 1280, 1024, 800, 640], v => v == 0 ? "不限制" : v.ToString(), D.Video.MaxSize, v => Set(d => d.Video.MaxSize = v))),
            Form.Section("輸入",
                Form.Combo("鍵盤模式", [KeyboardMode.Physical, KeyboardMode.Ime], KeyboardTitle, D.KeyboardMode, v => Set(d => d.KeyboardMode = v)),
                Form.Note("實體按鍵：依按鍵位置送出，不受輸入法影響（注音模式下打英文也正確）。\nWindows 輸入法：用 Windows 的注音等輸入法組字後送出文字。"),
                Form.Toggle("同步剪貼簿（電腦 ↔ 手機）", D.ClipboardSync, v => Set(d => d.ClipboardSync = v))),
            Form.Section("音訊",
                Form.Toggle("把手機聲音傳到電腦（Android 11 以上）", D.AudioEnabled, v => { Set(d => d.AudioEnabled = v); Show(); }),
                Form.Toggle("傳到電腦時手機不發出聲音", D.MuteDeviceWhenForwarding, v => Set(d => d.MuteDeviceWhenForwarding = v), D.AudioEnabled),
                Form.Note("關閉這個選項時，手機和電腦同時發聲（需要 Android 13 以上；較舊的手機一律靜音）。")),
            Form.Section("螢幕",
                Form.Toggle("鏡像時讓手機黑屏", D.TurnScreenOffWhileMirroring, v => Set(d => d.TurnScreenOffWhileMirroring = v)),
                Form.Toggle("偵測到鎖定畫面時，提供 Windows Hello 填入解鎖密碼", D.OfferHelloUnlock, v => Set(d => d.OfferHelloUnlock = v))));
    }

    public static string KeyboardTitle(KeyboardMode m) => m == KeyboardMode.Physical ? "實體按鍵" : "Windows 輸入法";

    // MARK: 儲存

    private FrameworkElement Storage()
    {
        var folder = AppSettings.SettingsFolder;
        var usage = Form.Value("目前使用", FileSize.Format(CacheManager.Shared.TotalBytes));
        CacheManager.Shared.Recalc();
        void Refresh() => usage.Children.OfType<TextBlock>().LastOrDefault()!.Text = FileSize.Format(CacheManager.Shared.TotalBytes);
        CacheManager.Shared.Changed += Refresh;
        Closed += (_, _) => CacheManager.Shared.Changed -= Refresh;

        var limit = new NumberBox { Value = D.CacheLimitMB, Minimum = 20, Maximum = 10000, SmallChange = 50, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 150 };
        limit.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) Set(d => d.CacheLimitMB = (int)e.NewValue); };
        var days = new NumberBox { Value = D.CacheClearDays, Minimum = 1, Maximum = 365, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 150 };
        days.ValueChanged += (_, e) => { if (!double.IsNaN(e.NewValue)) Set(d => d.CacheClearDays = (int)e.NewValue); };

        var cacheRows = new List<FrameworkElement>
        {
            usage,
            Form.Row("上限（MB）", limit),
            Form.Combo("清除時機", [CacheClearPolicy.OnDisconnect, CacheClearPolicy.AfterDays, CacheClearPolicy.Never],
                p => p switch { CacheClearPolicy.OnDisconnect => "裝置斷線時立即清除", CacheClearPolicy.AfterDays => "裝置幾天沒連線後清除", _ => "只依容量上限清除" },
                D.CacheClearPolicy, v => { Set(d => d.CacheClearPolicy = v); Show(); }),
        };
        if (D.CacheClearPolicy == CacheClearPolicy.AfterDays) cacheRows.Add(Form.Row("幾天沒連線就清除", days));
        cacheRows.Add(Form.Buttons(Form.Btn("立即套用", () => CacheManager.Shared.Enforce()), Form.Btn("清除全部快取", () => CacheManager.Shared.ClearAll())));

        var location = new List<FrameworkElement>
        {
            Form.Value("目前", folder ?? "這台電腦（本機）"),
            Form.Buttons(Form.Btn("選擇資料夾（例如 OneDrive）…", ChooseFolder), Form.Btn("改回本機", () => { AppSettings.SetSettingsFolder(null); Show(); })),
            Form.Toggle("密碼也存到這個資料夾", D.SyncSecrets, v => { Set(d => d.SyncSecrets = v); Show(); }, folder is not null),
            Form.Note(D.SyncSecrets
                ? "⚠️ 手機解鎖密碼、分享密碼會以明碼寫在 settings.json，並同步到所有使用同一個資料夾的電腦。"
                : "密碼只存在這台電腦（用 Windows 帳戶加密）。"),
            Form.Note("把 PhoneCentre.exe 帶到另一台電腦，選同一個 OneDrive 資料夾就能沿用設定。"),
        };

        return Stack(
            Form.Section("設定檔位置", [.. location]),
            Form.Section("簡訊、通知、通話記錄（透過 adb 讀取，不需在手機安裝程式）",
                Form.Toggle("定時收集並保存在這台電腦", D.PhoneDataEnabled, v => Set(d => d.PhoneDataEnabled = v)),
                RetentionRow("簡訊保存", D.SmsRetentionDays, v => Set(d => d.SmsRetentionDays = v)),
                RetentionRow("通知保存", D.NotificationRetentionDays, v => Set(d => d.NotificationRetentionDays = v)),
                RetentionRow("通話記錄保存", D.CallRetentionDays, v => Set(d => d.CallRetentionDays = v)),
                Form.Note("通知只能在手機連線期間記錄；手機上還在的通知不受保存天數影響，清掉之後才開始計算。超過天數的資料會自動刪除。")),
            Form.Section("快取（App 圖示、相片縮圖、預覽下載）", [.. cacheRows]));
    }

    private static FrameworkElement RetentionRow(string label, int days, Action<int> set)
    {
        int[] opts = [1, 3, 7, 14, 30, 60, 90, 180, 365, 0];
        var values = opts.Contains(days) ? opts : [.. opts, days];
        return Form.Combo(label, values, v => v == 0 ? "永久" : $"{v} 天", days, v => { set(v); PhoneDataStore.Shared.Prune(); });
    }

    private async void ChooseFolder()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var f = await picker.PickSingleFolderAsync();
        if (f is not null) { AppSettings.SetSettingsFolder(f.Path); Show(); }
    }
}
