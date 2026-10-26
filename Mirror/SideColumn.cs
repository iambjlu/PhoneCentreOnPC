using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Support;
using PhoneCentre.Settings;
using PhoneCentre.Ui;

namespace PhoneCentre.Mirror;

/// <summary>
/// 右側欄（對應 NavBar.swift 的 SideColumn）：狀態指示 ＋ App 列（現在用的 App、其他）＋ 導覽列（導覽項目、金剛三鍵）。
/// 視窗高度不夠時（橫向）整排移到視窗底部橫向排列。
/// </summary>
internal sealed class SideColumn : UserControl
{
    private readonly MirrorModel _m;
    private readonly MirrorWindow _w;
    private bool _horizontal;
    private readonly StatusIndicator _indicator = new(38);

    // 垂直版面的各塊（用來量測與分配空間）
    private readonly Grid _area = new();
    private readonly Border _currentPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _othersContent = new() { Spacing = 10, Padding = new Thickness(0, 10, 0, 10) };
    private ArrowScroll? _othersScroll;
    private Border? _othersPanel;
    private StackPanel _navItems = new() { Spacing = 1 }, _navKeys = new() { Spacing = 1 };
    private ArrowScroll? _navScroll;
    private Border _navPanel = new();
    private readonly StackPanel _appWindowPanel = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private string _appsSig = "", _navSig = "";

    private MirrorModel Model => _m;

    public SideColumn(MirrorModel model, MirrorWindow window)
    {
        _m = model; _w = window;
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        // 點一下狀態環降下通知中心（主螢幕）
        _indicator.Tapped += (_, _) => { if (!_m.IsVirtualDisplay) _m.Session?.Send(ControlMessage.ExpandNotifications()); };
        LocationSimulation.Shared.Changed += () => { _navSig = ""; Update(); };
        _m.Catalog.Changed += () => { _appsSig = ""; Update(); };
        SizeChanged += (_, _) => Reallocate();
        Build();
    }

    public void SetHorizontal(bool h)
    {
        if (_horizontal == h && Content is not null) return;
        _horizontal = h;
        Build();
    }

    private void Build()
    {
        _appsSig = ""; _navSig = "";
        Background = Ui.Ui.Hex(0x59000000);
        if (_horizontal) BuildHorizontal(); else BuildVertical();
        Update();
    }

    // MARK: 垂直

    private void BuildVertical()
    {
        _navScroll = null; _othersScroll = null; _othersPanel = null;
        var root = new Grid { Padding = new Thickness(8, 10, 8, 8), RowSpacing = 8 };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _indicator.HorizontalAlignment = HorizontalAlignment.Center; _indicator.Margin = new Thickness(0, 0, 0, 2);
        root.Children.Add(_indicator);

        _area.Children.Clear();
        var top = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        _currentPanel.Child = null;
        top.Children.Add(_currentPanel);
        _othersScroll = new ArrowScroll(_othersContent, false);
        _othersPanel = Ui.Ui.Panel(_othersScroll);
        top.Children.Add(_othersPanel);
        _area.Children.Add(top);

        // 獨立視窗：在欄位正中間顯示目前的 App
        _appWindowPanel.VerticalAlignment = VerticalAlignment.Center;
        _area.Children.Add(Ui.Ui.Panel(_appWindowPanel));
        ((FrameworkElement)_area.Children[^1]).VerticalAlignment = VerticalAlignment.Center;
        _area.Children[^1].Visibility = Visibility.Visible;

        // 導覽列：導覽項目在上，金剛三鍵（返回、主畫面、多工）固定在最下面
        _navScroll = new ArrowScroll(_navItems, false);
        var navStack = new StackPanel { Spacing = 1, Padding = new Thickness(0, 6, 0, 6), Children = { _navScroll, Ui.Ui.Divider(0.4), _navKeys } };
        _navPanel = Ui.Ui.Panel(navStack);
        _navPanel.VerticalAlignment = VerticalAlignment.Bottom;
        _area.Children.Add(_navPanel);
        Grid.SetRow(_area, 1);
        root.Children.Add(_area);
        Content = root;

        _othersContent.SizeChanged += (_, _) => Reallocate();
        _navItems.SizeChanged += (_, _) => Reallocate();
        _navKeys.SizeChanged += (_, _) => Reallocate();
        _currentPanel.SizeChanged += (_, _) => Reallocate();
        _area.SizeChanged += (_, _) => Reallocate();
    }

    /// <summary>空間分配：導覽項目優先；「其他」盤子最少留三個圖示。對應 SideColumn.allocate</summary>
    private void Reallocate()
    {
        if (_horizontal || _othersScroll is null || _navScroll is null) return;
        double H = _area.ActualHeight;
        double currentH = _currentPanel.Visibility == Visibility.Visible ? _currentPanel.ActualHeight : 0;
        double othersNatural = _othersPanel!.Visibility == Visibility.Visible ? _othersContent.ActualHeight : 0;
        double navItemsNatural = _navItems.ActualHeight, keysH = _navKeys.ActualHeight;
        double? others = null, nav = null;
        if (H > 0 && navItemsNatural > 0 && keysH > 0)
        {
            const double gap = 8;
            double navChrome = keysH + 5 + 2 + 12;
            bool hasCurrent = !_m.IsAppWindow && currentH > 0;
            double avail = H - (hasCurrent ? currentH + gap : 0);
            if (_m.IsAppWindow || othersNatural <= 0)
                nav = Math.Max(36, Math.Min(navItemsNatural, avail - navChrome - (_m.IsAppWindow ? 90 : 0)));
            else
            {
                double navTotal = navItemsNatural + navChrome;
                if (othersNatural + gap + navTotal <= avail) { others = othersNatural; nav = navItemsNatural; }
                else
                {
                    double minOthers = Math.Min(othersNatural, 3 * 38 + 2 * 10 + 20);   // 三個圖示
                    double o = Math.Min(othersNatural, Math.Max(minOthers, avail - gap - navTotal));
                    others = o; nav = Math.Max(36, Math.Min(navItemsNatural, avail - gap - o - navChrome));
                }
            }
        }
        _othersScroll.Height = others is { } oh ? Math.Max(36, oh) : double.NaN;
        _navScroll.Height = nav ?? double.NaN;
    }

    // MARK: 水平（視窗底部橫條）

    private void BuildHorizontal()
    {
        _navScroll = null; _othersScroll = null; _othersPanel = null;
        var grid = new Grid { Padding = new Thickness(8, 6, 8, 6), ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _indicator.HorizontalAlignment = HorizontalAlignment.Left; _indicator.Margin = new Thickness(0);
        _indicator.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_indicator);

        // 現在在用的 App，與其他（可左右捲動）；獨立視窗：現用的 App 放在狀態環與導覽盤子之間那塊空白的正中間
        Grid.SetColumn(_currentPanel, 1); _currentPanel.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_currentPanel);
        _othersScroll = new ArrowScroll(_othersContent, true);
        _othersPanel = Ui.Ui.Panel(_othersScroll);
        _othersPanel.HorizontalAlignment = HorizontalAlignment.Left; _othersPanel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_othersPanel, 2); grid.Children.Add(_othersPanel);
        _appWindowPanel.Orientation = Orientation.Horizontal;
        var appWin = Ui.Ui.Panel(_appWindowPanel); appWin.HorizontalAlignment = HorizontalAlignment.Center; appWin.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(appWin, 2); grid.Children.Add(appWin);

        // 導覽項目與金剛三鍵放在最右邊同一塊盤子裡，中間只用分隔線隔開；項目長度適中就好，放不下才捲動，金剛三鍵固定在最右邊
        _navScroll = new ArrowScroll(_navItems, true) { HorizontalAlignment = HorizontalAlignment.Left };
        var sep = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 28, Fill = Ui.Ui.Hex(0x59FFFFFF), Margin = new Thickness(6, 0, 6, 0) };
        var navRow = new StackPanel { Orientation = Orientation.Horizontal, Padding = new Thickness(6, 0, 6, 0), Children = { _navScroll, sep, _navKeys } };
        _navPanel = Ui.Ui.Panel(navRow);
        _navPanel.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_navPanel, 3); grid.Children.Add(_navPanel);
        Content = grid;
    }

    // MARK: 更新

    public void Update()
    {
        _indicator.Set(_m.StatusLoaded ? _m.Status : null);
        UpdateApps();
        UpdateNav();
        // App 已關閉（獨立視窗）：導覽列全部反灰、不能按
        _navPanel.IsHitTestVisible = !_m.AppClosed;
        _navPanel.Opacity = _m.AppClosed ? 0.4 : 1;
        Reallocate();
    }

    private void UpdateApps()
    {
        var cat = _m.Catalog;
        var (opened, unopened) = cat.BarOrder(AppSettings.Pinned(_m.Device.Key));
        var cur = cat.Foreground is { } f && opened.FirstOrDefault() == f ? f : null;
        var otherOpened = cur is null ? opened : opened.Skip(1).ToList();
        string[] shown = [.. (cur is null ? [] : new[] { cur }), .. otherOpened, "|", .. unopened];
        int icons = shown.Count(p => p != "|" && cat.Icon(p) is not null);
        var sig = $"{_horizontal}|{_m.IsAppWindow}|{_m.AppPackage}|{_m.AppLabel}|{string.Join(',', shown)}|{string.Join(',', cat.Running)}|{icons}";
        if (sig == _appsSig) return;
        _appsSig = sig;

        if (_m.IsAppWindow && _m.AppPackage is { } app)
        {
            // 獨立視窗：沒有其他 App 盤子
            _currentPanel.Visibility = Visibility.Collapsed;
            if (_othersPanel is not null) _othersPanel.Visibility = Visibility.Collapsed;
            _appWindowPanel.Children.Clear();
            var icon = Ui.Ui.AppIcon(cat, app, _horizontal ? 34 : 38);
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            _appWindowPanel.Children.Add(icon);
            var label = Ui.Ui.Text(_m.AppLabel ?? cat.Label(app), _horizontal ? 12 : 10, false, false);
            label.Opacity = 0.9; label.TextWrapping = _horizontal ? TextWrapping.NoWrap : TextWrapping.Wrap; label.TextAlignment = TextAlignment.Center;
            label.MaxWidth = _horizontal ? 140 : 56; label.VerticalAlignment = VerticalAlignment.Center;
            if (_horizontal) label.Margin = new Thickness(0, 0, 4, 0);
            _appWindowPanel.Children.Add(label);
            _appWindowPanel.Visibility = Visibility.Visible;
            _appWindowPanel.Padding = _horizontal ? new Thickness(8, 4, 8, 4) : new Thickness(4, 10, 4, 10);
            if (_horizontal) _appWindowPanel.Spacing = 8;
            // 垂直版：容器 Panel 一起顯示
            if (_appWindowPanel.Parent is Border b) b.Visibility = Visibility.Visible;
            return;
        }
        if (_appWindowPanel.Parent is Border pb) pb.Visibility = Visibility.Collapsed;
        _appWindowPanel.Visibility = Visibility.Collapsed;

        // 現在正在用的 App：獨立一塊盤子，完整顯示、不捲動
        if (cur is not null)
        {
            var row = new StackPanel { Orientation = _horizontal ? Orientation.Horizontal : Orientation.Vertical, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
            row.Children.Add(RailIcon(cur, true));
            if (_horizontal)
            {
                var t = Ui.Ui.Text(cat.Label(cur), 12, false, false); t.Opacity = 0.9; t.MaxWidth = 110; t.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(t);
            }
            row.Padding = _horizontal ? new Thickness(8, 4, 12, 4) : new Thickness(0, 10, 0, 10);
            _currentPanel.Child = row; _currentPanel.Visibility = Visibility.Visible;
            _currentPanel.Background = Ui.Ui.Hex(0x1FFFFFFF); _currentPanel.BorderBrush = Ui.Ui.Hex(0x26FFFFFF); _currentPanel.BorderThickness = new Thickness(1); _currentPanel.CornerRadius = new CornerRadius(22);
        }
        else _currentPanel.Visibility = Visibility.Collapsed;

        // 其他：已開啟的 → 分隔線 → 還沒開的喜好項目
        _othersContent.Children.Clear();
        _othersContent.Orientation = _horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _othersContent.Padding = _horizontal ? new Thickness(8, 4, 8, 4) : new Thickness(0, 10, 0, 10);
        foreach (var p in otherOpened) _othersContent.Children.Add(RailIcon(p, true));
        if (otherOpened.Count > 0 && unopened.Count > 0)
            _othersContent.Children.Add(_horizontal
                ? new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = 30, Fill = Ui.Ui.Hex(0x8CFFFFFF), Margin = new Thickness(0, 0, 0, 0) }
                : Ui.Ui.Divider(0.55));
        foreach (var p in unopened) _othersContent.Children.Add(RailIcon(p, false));
        if (_othersPanel is not null) _othersPanel.Visibility = otherOpened.Count + unopened.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // MARK: App 圖示（點一下啟動；拖出 App 列超過距離放開 → 在獨立視窗開啟）

    private const double TearOffDistance = 70;
    private string? _dragPkg;
    private (int X, int Y) _dragStart;
    private bool _dragging;

    private FrameworkElement RailIcon(string pkg, bool running)
    {
        var cat = _m.Catalog;
        var cell = new Grid { Width = _horizontal ? 44 : 56, Height = _horizontal ? 44 : 40, Background = Ui.Ui.Hex(0x01000000) };
        var icon = Ui.Ui.AppIcon(cat, pkg, 38);
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
        cell.Children.Add(icon);
        // 執行中標記：垂直時在右邊，水平時在下面；目前在最上層的拉長成圓角短線
        if (running)
        {
            bool current = pkg == cat.Foreground;
            var mark = new Border { CornerRadius = new CornerRadius(1.5), Background = Ui.Ui.Hex(0xCCFFFFFF) };
            if (_horizontal) { mark.Width = current ? 12 : 4; mark.Height = current ? 3 : 4; mark.HorizontalAlignment = HorizontalAlignment.Center; mark.VerticalAlignment = VerticalAlignment.Bottom; }
            else { mark.Width = current ? 3 : 4; mark.Height = current ? 12 : 4; mark.HorizontalAlignment = HorizontalAlignment.Right; mark.VerticalAlignment = VerticalAlignment.Center; mark.Margin = new Thickness(0, 0, 3, 0); }
            if (!current) mark.CornerRadius = new CornerRadius(2);
            cell.Children.Add(mark);
        }
        ToolTipService.SetToolTip(cell, cat.Label(pkg));

        cell.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(cell).Properties.IsLeftButtonPressed) return;
            cell.CapturePointer(e.Pointer);
            var c = DragGhost.CursorPosition; _dragStart = (c.X, c.Y); _dragPkg = pkg; _dragging = false;
        };
        cell.PointerMoved += (_, e) =>
        {
            if (_dragPkg != pkg || !e.Pointer.IsInContact) return;
            var c = DragGhost.CursorPosition;
            double s = XamlRoot?.RasterizationScale ?? 1;
            double dx = c.X - _dragStart.X, dy = c.Y - _dragStart.Y;
            if (!_dragging && Math.Sqrt(dx * dx + dy * dy) < 6 * s) return;
            _dragging = true; icon.Opacity = 0.4;
            DragGhost.Shared.Show(cat.Icon(pkg), Math.Abs(dx) > TearOffDistance * s, s);
        };
        cell.PointerReleased += (_, e) =>
        {
            if (_dragPkg != pkg) return;
            cell.ReleasePointerCapture(e.Pointer);
            _dragPkg = null; icon.Opacity = 1;
            if (!_dragging) { _m.Launch(pkg); return; }
            _dragging = false;
            DragGhost.Shared.Hide();
            var c = DragGhost.CursorPosition; double s = XamlRoot?.RasterizationScale ?? 1;
            if (Math.Abs(c.X - _dragStart.X) > TearOffDistance * s)
                WindowManager.OpenApp(_m.Device, pkg, (c.X - 30 * s, c.Y - 20 * s));   // 視窗左上角放在放開的位置
        };
        cell.PointerCanceled += (_, _) => { _dragPkg = null; _dragging = false; icon.Opacity = 1; DragGhost.Shared.Hide(); };

        var menu = new MenuFlyout();
        var stop = new MenuFlyoutItem { Text = "強制停止" }; stop.Click += async (_, _) => await cat.ForceStopAsync(pkg);
        var win = new MenuFlyoutItem { Text = "在獨立視窗開啟" }; win.Click += (_, _) => WindowManager.OpenApp(_m.Device, pkg);
        var pin = new MenuFlyoutItem { Text = AppSettings.IsPinned(pkg, _m.Device.Key) ? "移除喜好項目" : "加入喜好項目" };
        pin.Click += (_, _) => { AppSettings.TogglePin(pkg, _m.Device.Key); _appsSig = ""; Update(); };
        menu.Items.Add(stop); menu.Items.Add(win); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(pin);
        cell.ContextFlyout = menu;
        return cell;
    }

    // MARK: 導覽列

    /// <summary>選單裡的旋轉手機圖示：PathIcon 不會自動縮放 24×24 的路徑，所以用幾何變換先繞中心旋轉再縮到 16 px</summary>
    private static PathIcon RotatedPhone(double degrees)
    {
        var geo = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), MaterialIconData.Paths["smartphone"]);
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform { Angle = degrees, CenterX = 12, CenterY = 12 });
        group.Children.Add(new ScaleTransform { ScaleX = 16.0 / 24, ScaleY = 16.0 / 24 });
        geo.Transform = group;
        return new PathIcon { Data = geo, Width = 16, Height = 16 };
    }

    private void UpdateNav()
    {
        var loc = LocationSimulation.Shared.IsActive(_m.Device);
        var sig = $"{_horizontal}|{_m.KeyboardMode}|{_m.ScreenOff}|{loc}|{_m.IsAppWindow}|{_m.DesktopMode}|{_m.IsVirtualDisplay}|{_m.Status.AutoRotate}|{_m.Status.UserRotation}|{_m.Status.ForceOrientation}|{_m.Muted}";
        if (sig == _navSig) return;
        _navSig = sig;
        var orient = _horizontal ? Orientation.Horizontal : Orientation.Vertical;
        _navItems = new StackPanel { Orientation = orient, Spacing = 1 };
        _navKeys = new StackPanel { Orientation = orient, Spacing = 1 };
        void Add(UIElement e) => _navItems.Children.Add(e);

        var video = Ui.Ui.NavButton(new MIcon("tune", 18), "畫質設定", () => { });
        video.Flyout = Popovers.VideoSettings(_m);
        Add(video);

        var kb = Ui.Ui.NavButton(new MIcon(_m.KeyboardMode == KeyboardMode.Physical ? "keyboard" : "keyboard_alt", 18), $"輸入方式：{SettingsWindow.KeyboardTitle(_m.KeyboardMode)}", () => { });
        var kbMenu = new MenuFlyout();
        foreach (var mode in new[] { KeyboardMode.Physical, KeyboardMode.Ime })
        {
            var item = new RadioMenuFlyoutItem { Text = SettingsWindow.KeyboardTitle(mode), GroupName = "kb", IsChecked = _m.KeyboardMode == mode };
            item.Click += (_, _) => _m.SetKeyboardMode(mode);
            kbMenu.Items.Add(item);
        }
        kb.Flyout = kbMenu;
        Add(kb);

        if (_m.DesktopMode)
        {
            var launcher = Ui.Ui.NavButton(new MIcon("apps", 18), "在這個桌面開啟 App", () => { });
            launcher.Flyout = Popovers.MiniLauncher(_m, pkg => _m.Launch(pkg));
            Add(launcher);
        }
        // 獨立視窗（單一 App）不需要模擬定位，只有主螢幕與桌面模式有
        if (!_m.IsAppWindow)
            Add(Ui.Ui.NavButton(new MIcon(loc ? "location_off" : "location_on", 18), loc ? "模擬定位（模擬中）" : "模擬定位", () => WindowManager.OpenLocation(_m.Device)));

        var sound = Ui.Ui.NavButton(new MIcon("volume_up", 18), "聲音", () => { });
        sound.Flyout = Popovers.Sound(_m);
        Add(sound);
        Add(Ui.Ui.NavButton(new MIcon("photo_camera", 18), "截圖存到桌面", _m.Screenshot));

        var rot = Ui.Ui.NavButton(new MIcon("screen_rotation", 18), "旋轉", () => { });
        var rotMenu = new MenuFlyout();
        if (_m.IsVirtualDisplay)
        {
            var r = new MenuFlyoutItem { Text = "旋轉視窗方向" }; r.Click += (_, _) => _m.RotateVirtualDisplay();
            rotMenu.Items.Add(r);
        }
        else
        {
            (int Rot, string Name, double Deg)[] dirs = [(0, "直向", 0), (1, "橫向（向左）", 90), (2, "直向（倒立）", 180), (3, "橫向（向右）", 270)];
            foreach (var (rv, name, deg) in dirs)
            {
                var item = new RadioMenuFlyoutItem { Text = name, GroupName = "rot", Icon = RotatedPhone(deg), IsChecked = !_m.Status.AutoRotate && _m.Status.UserRotation == rv };
                item.Click += (_, _) => _m.SetRotation(rv);
                rotMenu.Items.Add(item);
            }
            rotMenu.Items.Add(new MenuFlyoutSeparator());
            var auto = new ToggleMenuFlyoutItem { Text = "自動旋轉", IsChecked = _m.Status.AutoRotate };
            auto.Click += (_, _) => _m.SetAutoRotate(auto.IsChecked);
            var force = new ToggleMenuFlyoutItem { Text = "強制所有 App 使用這個方向", IsChecked = _m.Status.ForceOrientation };
            force.Click += (_, _) => _m.SetForceOrientation(force.IsChecked);
            rotMenu.Items.Add(auto); rotMenu.Items.Add(force);
        }
        rot.Flyout = rotMenu;
        Add(rot);

        if (!_m.IsVirtualDisplay)
        {
            Add(Ui.Ui.NavButton(new MIcon(_m.ScreenOff ? "mobile_off" : "smartphone", 18), _m.ScreenOff ? "點亮手機螢幕" : "讓手機黑屏（繼續鏡像）", _m.ToggleScreenOff));
            Add(Ui.Ui.NavButton(new MIcon("power_settings_new", 18), "電源", () => _m.Key(KeyMap.Power)));
        }

        // 金剛三鍵：固定在最下面（獨立視窗：隱藏視窗、強制停止並關閉視窗）
        _navKeys.Children.Add(Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Back), "返回（右鍵／Esc）", _m.NavBack));
        _navKeys.Children.Add(_m.IsAppWindow
            ? Ui.Ui.NavButton(new MIcon("close", 18), "隱藏視窗（App 留在虛擬顯示器上繼續執行；再從 App 列開啟或點工作列圖示叫回來）", () => _m.OnHideWindow?.Invoke())
            : Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Home), _m.IsVirtualDisplay ? "桌面 Home" : "主畫面（中鍵）", _m.NavHome));
        _navKeys.Children.Add(_m.IsAppWindow
            ? Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Stop), "強制停止 App 並關閉視窗", _m.ForceStopAndClose)
            : Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Recents), "最近使用", _m.NavRecents));

        // 重新掛上（容器內的子元素換了）
        if (_horizontal) RebuildNavHost(); else RebuildNavHostVertical();
    }

    private void RebuildNavHost()
    {
        if (_navScroll is null) return;
        _navScroll.SetContent(_navItems);
        if (_navPanel.Child is StackPanel row) { row.Children.RemoveAt(row.Children.Count - 1); row.Children.Add(_navKeys); }
    }

    private void RebuildNavHostVertical()
    {
        if (_navScroll is null) return;
        _navScroll.SetContent(_navItems);
        if (_navPanel.Child is StackPanel stack) { stack.Children.RemoveAt(stack.Children.Count - 1); stack.Children.Add(_navKeys); }
        _navItems.SizeChanged += (_, _) => Reallocate();
        _navKeys.SizeChanged += (_, _) => Reallocate();
    }
}
