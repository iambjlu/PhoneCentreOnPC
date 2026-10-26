using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Adb;
using PhoneCentre.Pages;
using PhoneCentre.Services;
using PhoneCentre.Ui;
using Windows.Graphics;

namespace PhoneCentre;

public enum DeviceTab { Overview, Launchpad, Files, Notifications, Sms, Calls, Photos }

/// <summary>主視窗（對應 MainView.swift）：左邊固定 236 寬的裝置側邊欄，右邊是分頁列加內容</summary>
public sealed class MainWindow : Window
{
    private static readonly (DeviceTab Tab, string Title)[] Tabs =
    [
        (DeviceTab.Overview, "總覽"), (DeviceTab.Launchpad, "啟動台"), (DeviceTab.Files, "檔案"), (DeviceTab.Notifications, "通知"),
        (DeviceTab.Sms, "簡訊"), (DeviceTab.Calls, "通話記錄"), (DeviceTab.Photos, "相機膠卷"),
    ];

    private readonly DeviceManager _dm = DeviceManager.Shared;
    private string? _selection;
    private DeviceTab _tab = DeviceTab.Overview;
    private string _contentKey = "";

    private readonly StackPanel _deviceList = new() { Spacing = 2, Padding = new Thickness(8, 0, 8, 0) };
    private readonly Ellipse _serverDot = new() { Width = 7, Height = 7 };
    private readonly TextBlock _serverText = Ui.Ui.Text("", 12), _eventText = Ui.Ui.Text("", 11, true);
    private readonly TextBlock _title = Ui.Ui.Text("", 16, false, true), _subtitle = Ui.Ui.Text("", 12, true);
    private readonly ContentControl _host = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private SegmentedBar _tabBar = null!;
    private readonly Grid _toolbar = new();

    public MainWindow()
    {
        Title = "PhoneCentre";
        SystemBackdrop = new MicaBackdrop();
        // 預設 1100x880，但不超過工作區的 92%
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var size = new SizeInt32(Math.Min(1100, (int)(area.Width * 0.92)), Math.Min(880, (int)(area.Height * 0.92)));
        AppWindow.Resize(size);
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(236) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var side = BuildSidebar();
        root.Children.Add(side);
        var main = BuildMain();
        Grid.SetColumn(main, 1);
        root.Children.Add(main);
        Content = root;

        _dm.Changed += Refresh;
        DeviceStatusStore.Shared.Changed += RefreshSidebar;
        Closed += (_, _) =>
        {
            _dm.Changed -= Refresh; DeviceStatusStore.Shared.Changed -= RefreshSidebar;
        };
        Refresh();
    }

    // MARK: 版面

    private FrameworkElement BuildSidebar()
    {
        var g = new Grid { Background = Ui.Ui.Hex(0x14000000) };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var add = new Button { Content = new MIcon("add", 18), Padding = new Thickness(6), Background = null, BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(add, "加入無線裝置");
        add.Click += async (_, _) => await ShowAddDialog();
        var head = new Grid { Padding = new Thickness(18, 12, 10, 6) };
        head.Children.Add(Ui.Ui.Text("裝置", 12, true, true));
        add.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(add);
        g.Children.Add(head);

        var scroll = new ScrollViewer { Content = _deviceList };
        Grid.SetRow(scroll, 1);
        g.Children.Add(scroll);

        var bar = new StackPanel { Spacing = 2, Padding = new Thickness(12, 10, 12, 10) };
        bar.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _serverDot, _serverText } });
        bar.Children.Add(_eventText);
        Grid.SetRow(bar, 2);
        g.Children.Add(bar);
        return g;
    }

    private FrameworkElement BuildMain()
    {
        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _toolbar.Padding = new Thickness(20, 0, 20, 0);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _subtitle } };
        _toolbar.Children.Add(titles);
        _tabBar = new SegmentedBar(Tabs.Select(t => t.Title));
        _tabBar.SelectionChanged += i => { if (Tabs[i].Tab != _tab) { _tab = Tabs[i].Tab; UpdateContent(); } };
        Grid.SetColumn(_tabBar, 1);
        _tabBar.VerticalAlignment = VerticalAlignment.Center;
        _toolbar.Children.Add(_tabBar);
        g.Children.Add(_toolbar);

        Grid.SetRow(_host, 1);
        g.Children.Add(_host);
        return g;
    }

    /// <summary>切換分頁（測試用的 --tab 參數也走這裡）</summary>
    public void SelectTab(DeviceTab tab)
    {
        _tab = tab;
        _tabBar.Selected = Array.FindIndex(Tabs, t => t.Tab == tab);
        UpdateContent();
    }

    // MARK: 更新

    private IEnumerable<Device> LocalDevices => _dm.Devices.Where(d => !d.IsRemote);

    private void Refresh()
    {
        var list = _dm.Devices;
        if (_selection is null || !list.Any(d => d.Id == _selection))
            _selection = list.FirstOrDefault(d => d.IsOnline)?.Id ?? list.FirstOrDefault()?.Id ?? _selection;
        RefreshSidebar();
        UpdateContent();
    }

    private void RefreshSidebar()
    {
        _deviceList.Children.Clear();
        foreach (var d in LocalDevices) _deviceList.Children.Add(DeviceRow(d, d.Id == _selection));
        if (!LocalDevices.Any())
        {
            var hint = Ui.Ui.Text("插上已開啟 USB 偵錯的手機，或按 + 加入無線裝置", 12, true);
            hint.TextWrapping = TextWrapping.Wrap; hint.Margin = new Thickness(10, 6, 10, 0);
            _deviceList.Children.Add(hint);
        }
        (_serverDot.Fill, _serverText.Text) = _dm.ServerStatus switch
        {
            AdbServerStatus.Running => ((Brush)Ui.Ui.Hex(0xFF30D158), "adb server 執行中"),
            AdbServerStatus.Down => (Ui.Ui.Hex(0xFFFF9F0A), Settings.AppSettings.KeepAlive ? "adb server 停止，正在重新啟動…" : "adb server 未執行"),
            AdbServerStatus.MissingAdb => (Ui.Ui.Hex(0xFFFF453A), "找不到 adb"),
            _ => (Ui.Ui.Hex(0xFF8E8E93), "檢查中…"),
        };
        _eventText.Text = _dm.LastEvent;
        _eventText.Visibility = _dm.LastEvent.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private FrameworkElement DeviceRow(Device d, bool selected)
    {
        var fg = selected ? Ui.Ui.Hex(0xFFFFFFFF) : d.IsOnline ? Ui.Ui.Accent : Ui.Ui.Secondary;
        var icon = new MIcon(d.IsRemote ? "lan" : d.IsWireless ? "wifi" : "usb", 20) { Foreground = fg, VerticalAlignment = VerticalAlignment.Center };

        // 第一行名稱、第二行連線方式（無線位址或廠牌型號）、第三行 Android 版本
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Ui.Text(d.DisplayName, 14));
        texts.Children.Add(Ui.Ui.Text(d.IsOnline ? (d.IsWireless ? d.Serial : d.ModelLine) : d.StateDescription, 11, true));
        if (d.IsOnline && d.Info is { } info) texts.Children.Add(Ui.Ui.Text($"Android {info.AndroidVersion}", 11, true));

        FrameworkElement right;
        if (d.IsOnline)
        {
            var ind = new StatusIndicator(30);
            ind.Set(DeviceStatusStore.Shared.StatusFor(d));
            right = ind;
        }
        else right = new Ellipse { Width = 7, Height = 7, Fill = Ui.Ui.Hex(0xFFFF9F0A) };
        right.VerticalAlignment = VerticalAlignment.Center;

        var grid = new Grid { ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1); grid.Children.Add(texts);
        Grid.SetColumn(right, 2); grid.Children.Add(right);

        var row = new Border
        {
            Child = grid, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8),
            Background = selected ? Ui.Ui.Hex(0x660A84FF) : Ui.Ui.Hex(0x01000000),
        };
        row.Tapped += (_, _) => { _selection = d.Id; RefreshSidebar(); UpdateContent(); };
        if (d.IsOnline)
        {
            var menu = new MenuFlyout();
            var mirror = new MenuFlyoutItem { Text = "鏡像畫面" };
            mirror.Click += (_, _) => WindowManager.OpenMirror(d);
            menu.Items.Add(mirror);
            row.ContextFlyout = menu;
        }
        return row;
    }

    private void UpdateContent()
    {
        var d = _selection is null ? null : _dm.Device(_selection);
        string key = $"{_dm.ServerStatus == AdbServerStatus.MissingAdb}|{d?.Id}|{d?.IsOnline}|{_tab}";
        if (d is not null)
        {
            _title.Text = d.DisplayName; _subtitle.Text = d.Subtitle;
            _subtitle.Visibility = d.Subtitle.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        _toolbar.Visibility = d is { IsOnline: true } && _dm.ServerStatus != AdbServerStatus.MissingAdb ? Visibility.Visible : Visibility.Collapsed;
        if (key == _contentKey) return;
        _contentKey = key;

        if (_dm.ServerStatus == AdbServerStatus.MissingAdb) _host.Content = new AdbSetupView();
        else if (d is null) _host.Content = Message("尚未選擇裝置", "用 USB 連接已開啟「USB 偵錯」的 Android 手機，或按左上角 + 加入無線裝置。");
        else if (!d.IsOnline) _host.Content = Message(d.DisplayName, d.StateDescription);
        else _host.Content = _tab switch
        {
            DeviceTab.Overview => new OverviewPage(d.Id),
            DeviceTab.Launchpad => new LaunchpadPage(d.Id),
            _ => Message(Tabs.First(t => t.Tab == _tab).Title, "這個分頁尚未移植到 Windows 版。"),
        };
    }

    private static FrameworkElement Message(string title, string body) => new StackPanel
    {
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 8, MaxWidth = 420,
        Children =
        {
            new MIcon("smartphone", 48) { Foreground = Ui.Ui.Tertiary, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = title, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            new TextBlock { Text = body, Foreground = Ui.Ui.Secondary, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
        },
    };

    // MARK: 加入無線裝置

    private async Task ShowAddDialog()
    {
        var box = new TextBox { PlaceholderText = "例如 192.168.1.20:5555", Width = 320 };
        var result = Ui.Ui.Text("", 12, true);
        var dialog = new ContentDialog
        {
            Title = "加入無線裝置", PrimaryButtonText = "連線", CloseButtonText = "關閉", XamlRoot = Content.XamlRoot,
            Content = new StackPanel { Spacing = 10, Children = { Ui.Ui.Text("在手機的「開發人員選項 → 無線偵錯」查看位址與連接埠。", 12, true), box, result } },
        };
        dialog.PrimaryButtonClick += async (_, e) =>
        {
            e.Cancel = true;
            var addr = box.Text.Trim();
            if (addr.Length == 0) return;
            if (!addr.Contains(':')) addr += ":5555";
            result.Text = "連線中…";
            var (ok, msg) = await AdbClient.ConnectAsync(addr);
            result.Text = msg;
            await _dm.RefreshAsync();
            if (ok) dialog.Hide();
        };
        await dialog.ShowAsync();
    }
}
