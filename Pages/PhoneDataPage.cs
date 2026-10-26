using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Ui;

namespace PhoneCentre.Pages;

public enum DataKind { Notifications, Sms, Calls }

/// <summary>通知／簡訊／通話記錄各自一個分頁（對應 PhoneDataView.swift；通知再分「現在」與「所有」）</summary>
public sealed class PhoneDataPage : UserControl
{
    private static readonly int[] RetentionOptions = [1, 3, 7, 14, 30, 60, 90, 180, 365, 0];

    private readonly string _deviceId;
    private readonly DataKind _kind;
    private readonly AppCatalog _catalog;
    private int _scope;              // 通知：0 現在、1 所有
    private bool _showOngoing;
    private string _query = "";
    private string _signature = "";
    private string? _selectedConversation;

    private readonly Grid _root = new();
    private readonly ContentControl _body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
    private ScrollViewer? _listScroll;
    private Button? _refresh;
    private ProgressRing? _spin;

    private Device Device => DeviceManager.Shared.Device(_deviceId)!;
    private string KindTitle => _kind switch { DataKind.Notifications => "通知", DataKind.Sms => "簡訊", _ => "通話記錄" };

    public PhoneDataPage(string deviceId, DataKind kind)
    {
        _deviceId = deviceId; _kind = kind;
        _catalog = DeviceManager.Shared.Catalog(Device);
        Content = _root;
        Loaded += (_, _) =>
        {
            DeviceManager.Shared.Changed += OnDevices;
            PhoneDataStore.Shared.Changed += OnData;
            PhoneDataStore.Shared.SyncStateChanged += OnSyncState;
            _catalog.Changed += OnCatalog;
        };
        Unloaded += (_, _) =>
        {
            DeviceManager.Shared.Changed -= OnDevices;
            PhoneDataStore.Shared.Changed -= OnData;
            PhoneDataStore.Shared.SyncStateChanged -= OnSyncState;
            _catalog.Changed -= OnCatalog;
        };
        Build();
    }

    private bool _built;
    private bool Ready => DeviceManager.Shared.Device(_deviceId)?.Info is not null;

    /// <summary>裝置鍵優先用 ro.serialno：手機資訊還沒讀到之前不能建立，否則資料會存到用連線位址當鍵的另一個目錄</summary>
    private void OnDevices() { if (!_built && Ready) Build(); }
    private void OnData() => RenderContent();
    private void OnCatalog() { if (_kind == DataKind.Notifications) { _signature = ""; RenderContent(); } }
    private void OnSyncState()
    {
        bool syncing = PhoneDataStore.Shared.IsSyncing(Device);
        if (_refresh is not null) _refresh.IsEnabled = !syncing;
        if (_spin is not null) _spin.Visibility = syncing ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: 版面

    private void Build()
    {
        _root.Children.Clear(); _root.RowDefinitions.Clear();
        _built = false;
        if (!Ready) { _root.Children.Add(new ProgressRing { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }); return; }
        _built = true;
        if (!AppSettings.PhoneDataEnabled) { _root.Children.Add(Intro()); return; }
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.Children.Add(Header());
        Grid.SetRow(_body, 1); _root.Children.Add(_body);
        _signature = "";
        RenderContent();
        _ = PhoneDataStore.Shared.SyncAsync(Device);
    }

    /// <summary>還沒開啟收集時的說明卡片</summary>
    private FrameworkElement Intro()
    {
        var open = new Button { Content = "開啟", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Padding = new Thickness(26, 8, 26, 8), HorizontalAlignment = HorizontalAlignment.Center };
        open.Click += (_, _) => { AppSettings.PhoneDataEnabled = true; Build(); _ = PhoneDataStore.Shared.SyncAsync(Device); };
        var body = Ui.Ui.Text("PhoneCentre 會定時透過 adb 讀取手機上的簡訊、通知與通話記錄並保存在這台電腦，不需要在手機安裝任何程式。\n保存天數可在「設定 → 儲存」分別調整。這些是個人隱私資料，只存在這台電腦的本機。", 14, true);
        body.TextWrapping = TextWrapping.Wrap; body.TextAlignment = TextAlignment.Center; body.MaxWidth = 460;
        var card = new StackPanel { Spacing = 14, Padding = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        card.Children.Add(new MIcon("notifications", 44) { Foreground = Ui.Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Center });
        card.Children.Add(new TextBlock { Text = $"保存{KindTitle}", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center });
        card.Children.Add(body); card.Children.Add(open);
        return new Grid { Children = { Ui.Ui.Panel(card, 24) }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>跟啟動台一樣的單列控制列：左邊篩選，右邊保存期限、搜尋與動作</summary>
    private FrameworkElement Header()
    {
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (_kind == DataKind.Notifications)
        {
            var seg = new SegmentedBar(["現在", "所有"]) { };
            seg.Selected = _scope;
            seg.SelectionChanged += i => { _scope = i; _signature = ""; RenderContent(); };
            var ongoing = new CheckBox { Content = "顯示常駐通知", IsChecked = _showOngoing, MinWidth = 0 };
            ongoing.Click += (_, _) => { _showOngoing = ongoing.IsChecked == true; _signature = ""; RenderContent(); };
            left.Children.Add(seg); left.Children.Add(ongoing);
        }

        var retention = new ComboBox { MinWidth = 100 };
        int days = RetentionDays;
        var options = RetentionOptions.Contains(days) ? RetentionOptions : [.. RetentionOptions, days];
        foreach (var o in options) retention.Items.Add(o == 0 ? "永久" : $"{o} 天");
        retention.SelectedIndex = Array.IndexOf(options, days);
        retention.SelectionChanged += (_, _) =>
        {
            if (retention.SelectedIndex >= 0) { RetentionDays = options[retention.SelectedIndex]; PhoneDataStore.Shared.Prune(); }
        };

        var search = new TextBox { PlaceholderText = "搜尋", Width = 240, CornerRadius = new CornerRadius(16), Text = _query };
        search.TextChanged += (_, _) => { _query = search.Text.Trim(); _signature = ""; RenderContent(); };

        _spin = new ProgressRing { Width = 16, Height = 16, IsActive = true, Visibility = Visibility.Collapsed };
        _refresh = new Button { Content = new Grid { Width = 18, Height = 18, Children = { new MIcon("refresh", 16), _spin } }, Padding = new Thickness(8) };
        ToolTipService.SetToolTip(_refresh, "立即更新");
        _refresh.Click += async (_, _) => await PhoneDataStore.Shared.SyncAsync(Device);
        var more = new Button { Content = new MIcon("more_horiz", 18), Padding = new Thickness(8), Background = null, BorderThickness = new Thickness(0) };
        var menu = new MenuFlyout();
        var stop = new MenuFlyoutItem { Text = "停止收集" }; stop.Click += (_, _) => { AppSettings.PhoneDataEnabled = false; Build(); };
        var del = new MenuFlyoutItem { Text = "刪除已保存的資料…" }; del.Click += async (_, _) => await ConfirmDeleteAll();
        menu.Items.Add(stop); menu.Items.Add(del); more.Flyout = menu;

        // 欄位：左邊篩選 | 空白 | 保存期限 | 搜尋（空間不夠就縮，最寬 240）| 更新 | 更多
        var bar = new Grid { Padding = new Thickness(12), ColumnSpacing = 10 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 110, MaxWidth = 240 });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        var retentionBox = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { Ui.Ui.Text("保存期限", 14), retention } };
        Grid.SetColumn(retentionBox, 2); bar.Children.Add(retentionBox);
        search.Width = double.NaN; search.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(search, 3); bar.Children.Add(search);
        Grid.SetColumn(_refresh, 4); bar.Children.Add(_refresh);
        Grid.SetColumn(more, 5); bar.Children.Add(more);
        OnSyncState();
        return bar;
    }

    private int RetentionDays
    {
        get => _kind switch { DataKind.Notifications => AppSettings.NotificationRetentionDays, DataKind.Sms => AppSettings.SmsRetentionDays, _ => AppSettings.CallRetentionDays };
        set { switch (_kind) { case DataKind.Notifications: AppSettings.NotificationRetentionDays = value; break; case DataKind.Sms: AppSettings.SmsRetentionDays = value; break; default: AppSettings.CallRetentionDays = value; break; } }
    }

    private async Task ConfirmDeleteAll()
    {
        var d = new ContentDialog { Title = "刪除這台手機已保存的簡訊、通知與通話記錄？", PrimaryButtonText = "刪除", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Close };
        if (await d.ShowAsync() == ContentDialogResult.Primary) PhoneDataStore.Shared.DeleteAll(Device);
    }

    // MARK: 內容

    private bool Matches(params string[] fields) => _query.Length == 0 || fields.Any(f => f.Contains(_query, StringComparison.CurrentCultureIgnoreCase));

    private void RenderContent()
    {
        if (!AppSettings.PhoneDataEnabled || Device is null) return;
        var b = PhoneDataStore.Shared.Bundle(Device);
        // 資料沒變就不重畫（重畫會把捲動位置洗掉）
        var sig = $"{_kind}|{_scope}|{_showOngoing}|{_query}|{b.Sms.Count}|{b.Calls.Count}|{b.Notifications.Count}|{b.Notifications.Count(n => n.IsActive)}|{b.Notifications.Sum(n => n.Text.Length + n.Title.Length)}";
        if (sig == _signature) return;
        _signature = sig;
        double offset = _listScroll?.VerticalOffset ?? 0;
        switch (_kind)
        {
            case DataKind.Notifications: _body.Content = NotificationsView(b); break;
            case DataKind.Calls: _body.Content = CallsView(b); break;
            default: _body.Content = SmsView(b); break;
        }
        if (_listScroll is not null) { var sv = _listScroll; DispatcherQueue.TryEnqueue(() => sv.ChangeView(null, offset, null, true)); }
    }

    private static FrameworkElement Empty(string text) => new TextBlock { Text = text, Foreground = Ui.Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    /// <summary>跟簡訊對話列表同一種風格：一塊面板，列與列之間有分隔線</summary>
    private FrameworkElement ListPanel(IEnumerable<FrameworkElement> rows)
    {
        var stack = new StackPanel { Padding = new Thickness(0, 6, 0, 6) };
        bool first = true;
        foreach (var r in rows)
        {
            if (!first) stack.Children.Add(Ui.Ui.Divider(0.3));
            first = false;
            r.Margin = new Thickness(14, 10, 14, 10);
            stack.Children.Add(r);
        }
        _listScroll = new ScrollViewer { Content = stack };
        var panel = Ui.Ui.Panel(_listScroll, 22);
        panel.Margin = new Thickness(12, 0, 12, 12);
        return panel;
    }

    // 通知
    private FrameworkElement NotificationsView(PhoneDataBundle b)
    {
        var list = b.Notifications.Where(n => _scope == 1 || n.IsActive).Where(n => _showOngoing || !n.Ongoing)
            .Where(n => Matches(n.Title, n.Text, n.Package))
            .OrderByDescending(n => n.ClearedAt ?? n.FirstSeen).ToList();
        if (list.Count == 0) { _listScroll = null; return Empty(_scope == 0 ? "目前手機上沒有通知" : "沒有通知"); }
        _ = EnsureIcons(list.Select(n => n.Package).Distinct());
        return ListPanel(list.Select(NotificationRow));
    }

    private async Task EnsureIcons(IEnumerable<string> pkgs)
    {
        if (_catalog.Apps.Count == 0) await _catalog.ReloadAsync();
        _catalog.LoadIcons(pkgs);
    }

    private FrameworkElement NotificationRow(PhoneNotification n)
    {
        var date = n.FirstSeen.ToLocalTime().ToString("yyyy年M月d日 HH:mm") + (n.ClearedAt is { } c ? "　清除於 " + c.ToLocalTime().ToString("HH:mm") : "");
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var app = Ui.Ui.Text(_catalog.Label(n.Package), 13, false, true); app.Opacity = 0.8;
        var dt = Ui.Ui.Text(date, 13); dt.Opacity = 0.7; Grid.SetColumn(dt, 1);
        head.Children.Add(app); head.Children.Add(dt);
        var col = new StackPanel { Spacing = 3 };
        col.Children.Add(head);
        if (n.Title.Length > 0) col.Children.Add(new TextBlock { Text = n.Title, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        if (n.Text.Length > 0) col.Children.Add(new TextBlock { Text = n.Text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        var icon = Ui.Ui.AppIcon(_catalog, n.Package, 36); icon.VerticalAlignment = VerticalAlignment.Top;
        var row = new Grid { ColumnSpacing = 10, Background = Ui.Ui.Hex(0x01000000) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(icon); Grid.SetColumn(col, 1); row.Children.Add(col);
        row.Tapped += async (_, _) =>
        {
            var d = new ContentDialog
            {
                Title = $"要打開「{_catalog.Label(n.Package)}」嗎？", Content = "會在獨立視窗開啟這個 App。",
                PrimaryButtonText = "打開", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Primary,
            };
            if (await d.ShowAsync() == ContentDialogResult.Primary) WindowManager.OpenApp(Device, n.Package);
        };
        return row;
    }

    // 通話記錄
    private FrameworkElement CallsView(PhoneDataBundle b)
    {
        var list = b.Calls.Where(c => Matches(c.Name, c.Number)).OrderByDescending(c => c.Date).ToList();
        if (list.Count == 0) { _listScroll = null; return Empty("沒有通話記錄"); }
        return ListPanel(list.Select(CallRow));
    }

    private static string DurationText(int s) => s >= 3600 ? $"{s / 3600} 時 {s % 3600 / 60} 分" : s >= 60 ? $"{s / 60} 分 {s % 60} 秒" : $"{s} 秒";

    private FrameworkElement CallRow(PhoneCall c)
    {
        var red = Ui.Ui.Hex(0xFFFF453A);
        var icon = new MIcon(c.Icon, 22) { Foreground = c.IsMissed ? red : Ui.Ui.Secondary, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock { Text = c.Name.Length == 0 ? (c.Number.Length == 0 ? "未知號碼" : c.Number) : c.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        if (c.IsMissed) name.Foreground = red;
        var left = new StackPanel { Children = { name, Ui.Ui.Text(c.Name.Length == 0 ? c.TypeTitle : $"{c.Number} · {c.TypeTitle}", 12, true) } };
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { Ui.Ui.Text(c.Date.ToLocalTime().ToString("yyyy年M月d日 HH:mm"), 12) } };
        if (c.Duration > 0) right.Children.Add(Ui.Ui.Text(DurationText(c.Duration), 12, true));
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(icon);
        Grid.SetColumn(left, 1); row.Children.Add(left); Grid.SetColumn(right, 2); row.Children.Add(right);
        return row;
    }

    // 簡訊：仿「訊息」App，左邊依號碼分組的對話列表，右邊是氣泡對話
    private sealed record Conversation(string Id, List<PhoneSMS> Messages) { public PhoneSMS Last => Messages[^1]; }

    private FrameworkElement SmsView(PhoneDataBundle b)
    {
        var convs = b.Sms.GroupBy(m => m.Address)
            .Select(g => new Conversation(g.Key, g.OrderBy(m => m.Date).ToList()))
            .Where(c => _query.Length == 0 || c.Id.Contains(_query, StringComparison.CurrentCultureIgnoreCase) || c.Messages.Any(m => m.Body.Contains(_query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderByDescending(c => c.Last.Date).ToList();
        _listScroll = null;
        if (convs.Count == 0) return Empty("沒有簡訊");
        var current = convs.FirstOrDefault(c => c.Id == _selectedConversation) ?? convs[0];

        var grid = new Grid { Margin = new Thickness(12, 0, 12, 12), ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var list = Ui.Ui.Panel(ConversationList(convs, current), 22);
        grid.Children.Add(list);
        var thread = Ui.Ui.Panel(Thread(current), 22);
        Grid.SetColumn(thread, 1); grid.Children.Add(thread);
        return grid;
    }

    private static FrameworkElement Avatar(double size) => new Border
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Ui.Ui.Hex(0x59808080),
        Child = new MIcon("person", size * 0.55) { Foreground = Ui.Ui.Hex(0xE6FFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
    };

    private static string ShortDate(DateTimeOffset d) => d.ToLocalTime().Date == DateTime.Today ? d.ToLocalTime().ToString("HH:mm") : d.ToLocalTime().ToString("yyyy/M/d");

    private FrameworkElement ConversationList(List<Conversation> convs, Conversation current)
    {
        var stack = new StackPanel { Padding = new Thickness(8) };
        foreach (var c in convs)
        {
            bool on = c.Id == current.Id;
            var fg = on ? Ui.Ui.Hex(0xFFFFFFFF) : null;
            var sec = on ? Ui.Ui.Hex(0xD9FFFFFF) : Ui.Ui.Secondary;
            var addr = new TextBlock { Text = c.Id, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            if (fg is not null) addr.Foreground = fg;
            var date = new TextBlock { Text = ShortDate(c.Last.Date), FontSize = 12, Foreground = sec, VerticalAlignment = VerticalAlignment.Center };
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(addr); Grid.SetColumn(date, 1); top.Children.Add(date);
            var body = new TextBlock { Text = c.Last.Body, FontSize = 13, Foreground = sec, MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis };
            var texts = new StackPanel { Spacing = 2, Children = { top, body } };
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var av = Avatar(34); av.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(av); Grid.SetColumn(texts, 1); row.Children.Add(texts);
            var item = new Border { Child = row, Padding = new Thickness(10), CornerRadius = new CornerRadius(14), Background = on ? Ui.Ui.Accent : Ui.Ui.Hex(0x01000000) };
            item.Tapped += (_, _) => { _selectedConversation = c.Id; _signature = ""; RenderContent(); };
            stack.Children.Add(item);
            if (c != convs[^1]) { var d = Ui.Ui.Divider(0.3); d.Margin = new Thickness(56, 0, 10, 0); stack.Children.Add(d); }
        }
        return new ScrollViewer { Content = stack };
    }

    private static string SeparatorDate(DateTimeOffset d)
    {
        var l = d.ToLocalTime();
        return l.Year == DateTime.Now.Year ? l.ToString("M月d日 dddd HH:mm") : l.ToString("yyyy年M月d日 HH:mm");
    }

    private FrameworkElement Thread(Conversation c)
    {
        var head = new StackPanel { Spacing = 4, Padding = new Thickness(0, 10, 0, 10), HorizontalAlignment = HorizontalAlignment.Center };
        var av = Avatar(44); av.HorizontalAlignment = HorizontalAlignment.Center;
        head.Children.Add(av);
        head.Children.Add(new TextBlock { Text = c.Id, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
        head.Children.Add(Ui.Ui.Text($"簡訊 · {c.Messages.Count} 則", 12, true));
        ((TextBlock)head.Children[2]).HorizontalAlignment = HorizontalAlignment.Center;

        var msgs = new StackPanel { Spacing = 6, Padding = new Thickness(14) };
        for (int i = 0; i < c.Messages.Count; i++)
        {
            var m = c.Messages[i];
            if (i == 0 || (m.Date - c.Messages[i - 1].Date).TotalSeconds > 3600)
            {
                var sep = Ui.Ui.Text(SeparatorDate(m.Date), 12, true, true);
                sep.HorizontalAlignment = HorizontalAlignment.Center; sep.Margin = new Thickness(0, 10, 0, 2);
                msgs.Children.Add(sep);
            }
            var text = new TextBlock { Text = m.Body, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontWeight = m.IsSent ? Microsoft.UI.Text.FontWeights.Medium : Microsoft.UI.Text.FontWeights.Normal };
            if (m.IsSent) text.Foreground = Ui.Ui.Hex(0xFFFFFFFF);
            msgs.Children.Add(new Border
            {
                Child = text, Padding = new Thickness(13, 8, 13, 8), CornerRadius = new CornerRadius(18), MaxWidth = 520,
                Background = m.IsSent ? Ui.Ui.Hex(0xFF339E52) : Ui.Ui.Hex(0x40808080),
                HorizontalAlignment = m.IsSent ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                Margin = m.IsSent ? new Thickness(70, 0, 0, 0) : new Thickness(0, 0, 70, 0),
            });
        }
        var scroll = new ScrollViewer { Content = msgs };
        // 切換對話或有新訊息時捲到最新一則
        void ToEnd() => scroll.ChangeView(null, scroll.ScrollableHeight, null, true);
        scroll.Loaded += (_, _) => { ToEnd(); DispatcherQueue.TryEnqueue(ToEnd); };
        scroll.SizeChanged += (_, _) => ToEnd();

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(head);
        var line = Ui.Ui.Divider(0.25); line.Margin = new Thickness(0); Grid.SetRow(line, 1); root.Children.Add(line);
        Grid.SetRow(scroll, 2); root.Children.Add(scroll);
        return root;
    }
}
