using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.ApplicationModel.DataTransfer;

namespace PhoneCentre.Pages;

/// <summary>啟動台（對應 LaunchpadView.swift）：開啟方式、搜尋、喜好項目／已開啟（單排）、依 A–Z／ㄅ–ㄦ 分組的所有 App，右邊有索引列</summary>
public sealed class LaunchpadPage : UserControl
{
    private readonly string _deviceId;
    private readonly AppCatalog _catalog;
    private string _query = "";
    private string _signature = "";
    private readonly Dictionary<string, AppCell> _cells = new();
    private readonly Dictionary<string, FrameworkElement> _anchors = new();
    private List<string> _railKeys = [];

    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly StackPanel _content = new() { Spacing = 26, Padding = new Thickness(20, 20, 40, 20) };
    private readonly Border _rail = new();
    private readonly ProgressRing _loading = new() { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private DispatcherQueueTimer? _timer;

    private Device? Device => DeviceManager.Shared.Device(_deviceId);

    public LaunchpadPage(string deviceId)
    {
        _deviceId = deviceId;
        _catalog = DeviceManager.Shared.Catalog(DeviceManager.Shared.Device(deviceId)!);

        var target = new ComboBox { MinWidth = 190 };
        target.Items.Add("在獨立視窗開啟"); target.Items.Add("在手機主螢幕開啟");
        target.SelectedIndex = AppSettings.LaunchTarget == "SeparateWindow" ? 0 : 1;
        target.SelectionChanged += (_, _) => AppSettings.LaunchTarget = target.SelectedIndex == 0 ? "SeparateWindow" : "MainScreen";

        var search = new TextBox { PlaceholderText = "搜尋", Width = 240, CornerRadius = new CornerRadius(16) };
        search.TextChanged += (_, _) => { _query = search.Text.Trim(); Render(); };
        var refresh = new Button { Content = new MIcon("refresh", 16), Padding = new Thickness(8) };
        ToolTipService.SetToolTip(refresh, "重新整理");
        refresh.Click += async (_, _) => { await _catalog.ReloadAsync(); await _catalog.RefreshTasksAsync(); };

        var bar = new Grid { Padding = new Thickness(12), ColumnSpacing = 12 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(target);
        Grid.SetColumn(search, 2); bar.Children.Add(search);
        Grid.SetColumn(refresh, 3); bar.Children.Add(refresh);

        _scroll.Content = _content;
        var body = new Grid();
        body.Children.Add(_scroll);
        BuildRailShell();
        body.Children.Add(_rail);
        body.Children.Add(_loading);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Content = root;

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        Render();
    }

    private void Start()
    {
        _catalog.Changed += Render;
        _ = LoadAsync();
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += (_, _) => _ = _catalog.RefreshTasksAsync();
        _timer.Start();
    }

    private void Stop() { _catalog.Changed -= Render; _timer?.Stop(); }

    private async Task LoadAsync()
    {
        if (_catalog.Apps.Count == 0) await _catalog.ReloadAsync();
        await _catalog.RefreshTasksAsync();
    }

    // MARK: 畫面

    private IEnumerable<AppEntry> Entries(IEnumerable<string> pkgs) =>
        pkgs.Select(p => _catalog.Apps.FirstOrDefault(a => a.Package == p)).OfType<AppEntry>();

    private void Render()
    {
        if (Device is not { } d) return;
        _loading.Visibility = _catalog.Loading && _catalog.Apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var (fav, open) = _catalog.Sections(AppSettings.Pinned(d.Key));
        var sig = $"{_query}|{_catalog.Apps.Count}|{string.Join(',', fav)}|{string.Join(',', open)}";
        if (sig == _signature)
        {
            foreach (var c in _cells.Values) c.Refresh();   // 只更新圖示與執行中標記，不重建（捲動位置不會跑掉）
            return;
        }
        _signature = sig;
        double offset = _scroll.VerticalOffset;

        _content.Children.Clear(); _cells.Clear(); _anchors.Clear();
        var pinnedApps = Entries(fav).ToList();
        var openApps = Entries(open).ToList();
        var taken = pinnedApps.Concat(openApps).Select(a => a.Package).ToHashSet();
        var filtered = _catalog.Apps.Where(a => _query.Length == 0
            || a.Label.Contains(_query, StringComparison.CurrentCultureIgnoreCase)
            || a.Package.Contains(_query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        if (_query.Length == 0)
        {
            Section("喜好項目", pinnedApps, false, true);
            Section("已開啟", openApps, false, true);
            var groups = filtered.Where(a => !taken.Contains(a.Package))
                .GroupBy(a => AppIndex.Key(a.Label)).OrderBy(g => AppIndex.Rank(g.Key)).ToList();
            if (groups.Count > 0 && (pinnedApps.Count > 0 || openApps.Count > 0))
                _content.Children.Add(Ui.Ui.Text("所有 App", 15, true, true));
            foreach (var g in groups)
                Section(g.Key, g.OrderBy(a => a.Label, AppIndex.NameComparer).ToList(), true, false, g.Key);
            _railKeys = groups.Count > 1 ? groups.Select(g => g.Key).ToList() : [];
        }
        else
        {
            Section(null, filtered.OrderBy(a => a.Label, AppIndex.NameComparer).ToList(), false, false);
            _railKeys = [];
        }
        RebuildRail();
        DispatcherQueue.TryEnqueue(() => _scroll.ChangeView(null, offset, null, true));
    }

    private void Section(string? title, List<AppEntry> apps, bool small, bool singleRow, string? anchor = null)
    {
        if (apps.Count == 0) return;
        var col = new StackPanel { Spacing = 14 };
        if (title is not null)
        {
            var t = Ui.Ui.Text(title, small ? 13 : 15, true, true);
            col.Children.Add(t);
            if (anchor is not null) _anchors[anchor] = col;
        }
        if (singleRow)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 18 };
            foreach (var a in apps) row.Children.Add(Cell(a));
            col.Children.Add(new ArrowScroll(row));
        }
        else
        {
            var grid = new ItemsRepeater
            {
                ItemsSource = apps.Select(Cell).ToList(),
                Layout = new UniformGridLayout { MinItemWidth = 96, MinItemHeight = 108, MinColumnSpacing = 18, MinRowSpacing = 14, ItemsJustification = UniformGridLayoutItemsJustification.Start },
            };
            col.Children.Add(grid);
        }
        _content.Children.Add(col);
    }

    private AppCell Cell(AppEntry app)
    {
        var cell = new AppCell(_catalog, app.Package, app.Label, 64, 96);
        cell.Refresh();
        _cells[app.Package] = cell;
        cell.Tapped += (_, _) => { if (Device is { } d) WindowManager.Launch(d, app.Package); };
        cell.ContextFlyout = Menu(app);
        return cell;
    }

    /// <summary>選單順序與 App 列相同：強制停止、在獨立視窗開啟在最上面，動到喜好項目的放最下面</summary>
    private MenuFlyout Menu(AppEntry app)
    {
        var d = Device!;
        var m = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action act) { var i = new MenuFlyoutItem { Text = text }; i.Click += (_, _) => act(); return i; }
        m.Items.Add(Item("強制停止", async () => await _catalog.ForceStopAsync(app.Package)));
        m.Items.Add(Item("在獨立視窗開啟", () => WindowManager.OpenApp(d, app.Package)));
        m.Items.Add(Item("在手機主螢幕開啟", () => WindowManager.LaunchOnMainScreen(d, app.Package)));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Item("在手機上顯示 App 資訊", async () => await _catalog.OpenAppInfoAsync(app.Package)));
        m.Items.Add(Item("拷貝套件名稱", () =>
        {
            var pkg = new DataPackage(); pkg.SetText(app.Package); Clipboard.SetContent(pkg);
        }));
        m.Items.Add(Item("解除安裝…", async () => await ConfirmUninstall(app)));
        m.Items.Add(new MenuFlyoutSeparator());
        m.Items.Add(Item(AppSettings.IsPinned(app.Package, d.Key) ? "移除喜好項目" : "加入喜好項目", () =>
        {
            AppSettings.TogglePin(app.Package, d.Key); _signature = ""; Render();
        }));
        return m;
    }

    private async Task ConfirmUninstall(AppEntry app)
    {
        var dialog = new ContentDialog
        {
            Title = $"解除安裝「{app.Label}」？", PrimaryButtonText = "解除安裝", CloseButtonText = "取消",
            XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await _catalog.UninstallAsync(app.Package);
    }

    // MARK: 索引列（A–Z／ㄅ–ㄦ，點或拖曳可快速捲到該組）

    private StackPanel _railStack = new();

    private void BuildRailShell()
    {
        _rail.HorizontalAlignment = HorizontalAlignment.Right; _rail.VerticalAlignment = VerticalAlignment.Center;
        _rail.Margin = new Thickness(0, 0, 8, 0); _rail.Width = 22; _rail.CornerRadius = new CornerRadius(11);
        _rail.Background = Ui.Ui.Hex(0x33808080); _rail.Visibility = Visibility.Collapsed;
        _rail.PointerPressed += (_, e) => { _rail.CapturePointer(e.Pointer); JumpTo(e); };
        _rail.PointerMoved += (_, e) => { if (e.GetCurrentPoint(_rail).Properties.IsLeftButtonPressed) JumpTo(e); };
        _rail.PointerReleased += (_, e) => _rail.ReleasePointerCapture(e.Pointer);
    }

    private void RebuildRail()
    {
        _rail.Visibility = _railKeys.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _railStack = new StackPanel();
        double step = _railKeys.Count == 0 ? 16 : Math.Max(Math.Min(Math.Max(_scroll.ActualHeight - 40, 300) / _railKeys.Count, 16), 9);
        foreach (var k in _railKeys)
        {
            var t = Ui.Ui.Text(k, Math.Min(step - 1, 11), true, true);
            t.TextAlignment = TextAlignment.Center; t.HorizontalAlignment = HorizontalAlignment.Center; t.Height = step;
            t.LineHeight = step; t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
            _railStack.Children.Add(t);
        }
        _rail.Child = _railStack;
    }

    private void JumpTo(PointerRoutedEventArgs e)
    {
        if (_railKeys.Count == 0 || _railStack.ActualHeight <= 0) return;
        double y = e.GetCurrentPoint(_railStack).Position.Y;
        int i = Math.Clamp((int)(y / (_railStack.ActualHeight / _railKeys.Count)), 0, _railKeys.Count - 1);
        if (!_anchors.TryGetValue(_railKeys[i], out var anchor)) return;
        double top = anchor.TransformToVisual(_content).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
        _scroll.ChangeView(null, Math.Max(0, top - 8), null, true);
    }
}
