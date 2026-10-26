using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Adb;
using PhoneCentre.Mirror;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace PhoneCentre;

/// <summary>
/// 鏡像視窗（對應 MirrorContentView.swift／NavBar.swift）：左邊是手機畫面，右邊 76 寬的控制欄
/// （狀態環、現在用的 App、其他 App、導覽項目、金剛三鍵）。
/// </summary>
public sealed class MirrorWindow : Window
{
    private const double RailWidth = 76;

    private readonly Device _device;
    private readonly AppCatalog _catalog;
    private ScrcpySession? _session;
    private H264Decoder? _decoder;
    private WriteableBitmap? _bitmap;
    private (int W, int H) _frame;
    private bool _sized, _pressed, _screenOff;
    private DispatcherQueueTimer? _taskTimer, _toastTimer;

    private readonly Image _video = new() { Stretch = Stretch.Uniform };
    private readonly UserControl _surface = new() { IsTabStop = true, Background = Ui.Ui.Hex(0x01000000) };
    private readonly TextBlock _status = Ui.Ui.Text("", 14, true);
    private readonly Border _toast = new();
    private readonly TextBlock _toastText = Ui.Ui.Text("", 13);
    private readonly StatusIndicator _indicator = new(38);
    private readonly StackPanel _appsHost = new() { Spacing = 8 };
    private readonly StackPanel _navHost = new();
    private Button? _screenOffButton;

    public MirrorWindow(Device device)
    {
        _device = device;
        _catalog = DeviceManager.Shared.Catalog(device);
        Title = device.DisplayName;
        PhoneCentre.Support.WindowIcon.Apply(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.Resize(new SizeInt32(520, 1000));

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailWidth) });

        // 手機畫面區域是黑底（不透出毛玻璃）
        var screen = new Grid { Background = Ui.Ui.Hex(0xFF000000) };
        screen.Children.Add(_video);
        _surface.Content = new Grid();
        screen.Children.Add(_surface);
        _status.HorizontalAlignment = HorizontalAlignment.Left; _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Margin = new Thickness(12); _status.TextWrapping = TextWrapping.Wrap;
        screen.Children.Add(_status);
        _toast.Child = _toastText;
        _toast.Background = Ui.Ui.Hex(0xCC202020); _toast.CornerRadius = new CornerRadius(12); _toast.Padding = new Thickness(14, 8, 14, 8);
        _toast.HorizontalAlignment = HorizontalAlignment.Center; _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 24); _toast.Visibility = Visibility.Collapsed; _toast.IsHitTestVisible = false;
        screen.Children.Add(_toast);
        root.Children.Add(screen);

        var rail = BuildRail();
        Grid.SetColumn(rail, 1);
        root.Children.Add(rail);
        Content = root;

        _surface.PointerPressed += (_, e) => { _surface.Focus(FocusState.Pointer); OnPointer(e, ControlMessage.MotionAction.Down); };
        _surface.PointerMoved += (_, e) => { if (_pressed) OnPointer(e, ControlMessage.MotionAction.Move); };
        _surface.PointerReleased += (_, e) => OnPointer(e, ControlMessage.MotionAction.Up);
        _surface.PointerCanceled += (_, e) => { if (_pressed) OnPointer(e, ControlMessage.MotionAction.Up); };
        _surface.PointerWheelChanged += OnWheel;
        _surface.KeyDown += (_, e) => OnKey(e, ControlMessage.KeyAction.Down);
        _surface.KeyUp += (_, e) => OnKey(e, ControlMessage.KeyAction.Up);

        DeviceStatusStore.Shared.Changed += UpdateStatus;
        _catalog.Changed += RebuildApps;
        Closed += (_, _) => Shutdown();

        var q = DispatcherQueue.GetForCurrentThread();
        _taskTimer = q.CreateTimer(); _taskTimer.Interval = TimeSpan.FromSeconds(3);
        _taskTimer.Tick += (_, _) => _ = _catalog.RefreshTasksAsync();
        _taskTimer.Start();
        _toastTimer = q.CreateTimer(); _toastTimer.IsRepeating = false;
        _toastTimer.Tick += (_, _) => _toast.Visibility = Visibility.Collapsed;

        UpdateStatus();
        RebuildApps();
        _ = _catalog.ReloadAsync().ContinueWith(_ => q.TryEnqueue(() => _ = _catalog.RefreshTasksAsync()));
        StartSession();
    }

    // MARK: 右側欄

    private FrameworkElement BuildRail()
    {
        var rail = new Grid { Background = Ui.Ui.Hex(0x40000000), Padding = new Thickness(8, 10, 8, 8), RowSpacing = 8 };
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _indicator.HorizontalAlignment = HorizontalAlignment.Center;
        // 點一下狀態環降下通知中心
        _indicator.Tapped += (_, _) => _session?.Send(ControlMessage.ExpandNotifications());
        rail.Children.Add(_indicator);

        var appsScroll = new ScrollViewer { Content = _appsHost, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollMode = ScrollMode.Auto };
        Grid.SetRow(appsScroll, 1);
        rail.Children.Add(appsScroll);

        BuildNav();
        var navPanel = Ui.Ui.Panel(_navHost);
        Grid.SetRow(navPanel, 2);
        rail.Children.Add(navPanel);
        return rail;
    }

    /// <summary>App 列：現在用的 App 一塊盤子，其他已開啟的與喜好項目另一塊（已開啟的在前、分隔線、還沒開的在後）</summary>
    private void RebuildApps()
    {
        _appsHost.Children.Clear();
        var (opened, unopened) = _catalog.BarOrder(AppSettings.Pinned(_device.Key));
        var cur = _catalog.Foreground;
        if (cur is not null)
            _appsHost.Children.Add(Ui.Ui.Panel(new StackPanel { Padding = new Thickness(0, 8, 0, 8), Children = { AppButton(cur) } }));
        var others = opened.Where(p => p != cur).ToList();
        if (others.Count + unopened.Count == 0) return;
        var col = new StackPanel { Spacing = 4, Padding = new Thickness(0, 8, 0, 8) };
        foreach (var p in others) col.Children.Add(AppButton(p));
        if (others.Count > 0 && unopened.Count > 0) col.Children.Add(Ui.Ui.Divider(0.3));
        foreach (var p in unopened) col.Children.Add(AppButton(p));
        _appsHost.Children.Add(Ui.Ui.Panel(col));
    }

    private FrameworkElement AppButton(string pkg)
    {
        var grid = new Grid { Width = 52, Height = 46, Background = Ui.Ui.Hex(0x01000000), HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Ui.Ui.AppIcon(_catalog, pkg, 38);
        icon.HorizontalAlignment = HorizontalAlignment.Center; icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);
        // 執行中的右邊有小圓點，目前在最上層的拉長成圓角短線
        if (_catalog.Running.Contains(pkg))
        {
            var mark = new Microsoft.UI.Xaml.Controls.Border
            {
                Width = 3, Height = pkg == _catalog.Foreground ? 14 : 3, CornerRadius = new CornerRadius(1.5),
                Background = Ui.Ui.Hex(0xCCFFFFFF), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            };
            grid.Children.Add(mark);
        }
        ToolTipService.SetToolTip(grid, _catalog.Label(pkg));
        grid.Tapped += (_, _) => Launch(pkg);

        var menu = new MenuFlyout();
        var stop = new MenuFlyoutItem { Text = "強制停止" }; stop.Click += async (_, _) => await _catalog.ForceStopAsync(pkg);
        var pin = new MenuFlyoutItem { Text = AppSettings.IsPinned(pkg, _device.Key) ? "移除喜好項目" : "加入喜好項目" };
        pin.Click += (_, _) => { AppSettings.TogglePin(pkg, _device.Key); RebuildApps(); };
        menu.Items.Add(stop); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(pin);
        grid.ContextFlyout = menu;
        return grid;
    }

    /// <summary>導覽列：導覽項目在上，金剛三鍵（返回、主畫面、多工）固定在最下面</summary>
    private void BuildNav()
    {
        _navHost.Padding = new Thickness(0, 6, 0, 6);
        _navHost.Spacing = 1;
        void Item(UIElement el) { _navHost.Children.Add(el); }
        Item(Ui.Ui.NavButton(new MIcon("tune", 18), "畫質設定", () => ShowToast("畫質設定尚未移植到 Windows 版")));
        Item(Ui.Ui.NavButton(new MIcon("keyboard", 18), "輸入方式", () => ShowToast("輸入方式尚未移植到 Windows 版")));
        Item(Ui.Ui.NavButton(new MIcon("location_on", 18), "模擬定位", () => ShowToast("模擬定位尚未移植到 Windows 版")));
        Item(Ui.Ui.NavButton(new MIcon("volume_up", 18), "聲音", () => ShowToast("聲音尚未移植到 Windows 版")));
        Item(Ui.Ui.NavButton(new MIcon("photo_camera", 18), "截圖存到桌面", Screenshot));
        Item(RotateButton());
        _screenOffButton = Ui.Ui.NavButton(new MIcon("smartphone", 18), "讓手機黑屏（繼續鏡像）", ToggleScreenOff);
        Item(_screenOffButton);
        Item(Ui.Ui.NavButton(new MIcon("power_settings_new", 18), "電源", () => TapKey(26)));
        _navHost.Children.Add(Ui.Ui.Divider(0.4));
        Item(Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Back), "返回（右鍵／Esc）", () => TapKey(4)));
        Item(Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Home), "主畫面（中鍵）", () => TapKey(3)));
        Item(Ui.Ui.NavButton(new SamsungIcon(SamsungIcon.Kind.Recents), "最近使用", () => TapKey(187)));
    }

    private Button RotateButton()
    {
        var b = Ui.Ui.NavButton(new MIcon("screen_rotation", 18), "旋轉", () => { });
        var menu = new MenuFlyout();
        string[] names = ["直向", "橫向（向左）", "直向（倒立）", "橫向（向右）"];
        for (int r = 0; r < 4; r++)
        {
            int rot = r;
            var item = new MenuFlyoutItem { Text = names[r] };
            item.Click += (_, _) => _ = RunShell($"settings put system accelerometer_rotation 0; settings put system user_rotation {rot}");
            menu.Items.Add(item);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var auto = new MenuFlyoutItem { Text = "自動旋轉" };
        auto.Click += (_, _) => _ = RunShell("settings put system accelerometer_rotation 1");
        var force = new MenuFlyoutItem { Text = "強制所有 App 使用這個方向" };
        force.Click += (_, _) => _ = RunShell("wm set-ignore-orientation-request true; wm fixed-to-user-rotation enabled");
        var unforce = new MenuFlyoutItem { Text = "恢復各 App 自己決定方向" };
        unforce.Click += (_, _) => _ = RunShell("wm set-ignore-orientation-request false; wm fixed-to-user-rotation default");
        menu.Items.Add(auto); menu.Items.Add(force); menu.Items.Add(unforce);
        b.Flyout = menu;
        return b;
    }

    private async Task RunShell(string cmd) { try { await _device.Client.ShellAsync(cmd); } catch { } }

    // MARK: 狀態

    private void UpdateStatus() => _indicator.Set(DeviceStatusStore.Shared.StatusFor(_device));

    private void ShowToast(string text)
    {
        _toastText.Text = text;
        _toast.Visibility = Visibility.Visible;
        _toastTimer?.Stop(); _toastTimer!.Interval = TimeSpan.FromSeconds(3); _toastTimer.Start();
    }

    // MARK: 工作階段與影像

    private void StartSession()
    {
        try
        {
            _decoder = new H264Decoder();
            _session = new ScrcpySession(_device, new MirrorConfig { Audio = false });
            _session.StateChanged += st =>
            {
                Log.Write($"狀態 {st.Kind} {st.Message}");
                _status.Text = st.Kind == ScrcpySession.StateKind.Running ? "" : $"{(st.Kind == ScrcpySession.StateKind.Failed ? "連線失敗" : "連線中…")} {st.Message}";
            };
            _session.FrameSizeChanged += (w, h) => { Log.Write($"畫面大小 {w}x{h}"); _frame = (w, h); FitWindow(w, h); };
            _session.VideoConfig += p => _decoder?.Config(p);
            _session.VideoFrame += (p, _, key) => _decoder?.Decode(p, key);
            _session.DeviceClipboard += _ => { };
            CompositionTarget.Rendering += OnRendering;
            _session.Start();
        }
        catch (Exception e)
        {
            Log.Write("錯誤 " + e);
            _status.Text = e.Message;
        }
    }

    /// <summary>第一次知道手機畫面大小時，把視窗調成合適的比例（高度不超過工作區的 90%）</summary>
    private void FitWindow(int w, int h)
    {
        if (_sized || Content.XamlRoot is not { } xr) return;
        _sized = true;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double scale = xr.RasterizationScale;
        int clientH = (int)Math.Min(area.Height * 0.88 - 40, 1000 * scale);
        int videoW = (int)(clientH * (double)w / h);
        AppWindow.ResizeClient(new SizeInt32(videoW + (int)(RailWidth * scale), clientH));
    }

    /// <summary>每個畫面刷新取一次解碼器的最新幀，寫進 WriteableBitmap</summary>
    private void OnRendering(object? sender, object e)
    {
        _decoder?.TryTakeFrame((bgra, w, h) =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h);
                _video.Source = _bitmap;
                _frame = (w, h);
            }
            using var stream = _bitmap.PixelBuffer.AsStream();
            stream.Write(bgra, 0, w * h * 4);
            _bitmap.Invalidate();
        });
    }

    private void Shutdown()
    {
        CompositionTarget.Rendering -= OnRendering;
        DeviceStatusStore.Shared.Changed -= UpdateStatus;
        _catalog.Changed -= RebuildApps;
        _taskTimer?.Stop();
        if (_screenOff) _session?.Send(ControlMessage.SetDisplayPower(true));
        _session?.Stop();
        _decoder?.Dispose();
    }

    // MARK: 動作

    public void Launch(string pkg) => _session?.Send(ControlMessage.StartApp(pkg));

    private void TapKey(int code)
    {
        _session?.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Down, code));
        _session?.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Up, code));
    }

    private void ToggleScreenOff()
    {
        _screenOff = !_screenOff;
        _session?.Send(ControlMessage.SetDisplayPower(!_screenOff));
        if (_screenOffButton is not null)
        {
            _screenOffButton.Content = new MIcon(_screenOff ? "mobile_off" : "smartphone", 18);
            ToolTipService.SetToolTip(_screenOffButton, _screenOff ? "點亮手機螢幕" : "讓手機黑屏（繼續鏡像）");
        }
    }

    /// <summary>主螢幕用 screencap，存到桌面</summary>
    private async void Screenshot()
    {
        try
        {
            var data = await _device.Client.ExecAsync("screencap -p", 20);
            if (data.Length < 100 || data[0] != 0x89 || data[1] != 0x50) { ShowToast("截圖失敗"); return; }
            var name = $"{_device.DisplayName} 截圖 {DateTime.Now:yyyy-MM-dd HH.mm.ss}.png";
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name);
            await File.WriteAllBytesAsync(path, data);
            ShowToast($"已存到桌面：{name}");
        }
        catch { ShowToast("截圖失敗"); }
    }

    // MARK: 輸入（滑鼠左鍵＝觸控、右鍵＝返回、中鍵＝主畫面、滾輪＝捲動）

    private bool ToPhone(Windows.Foundation.Point p, out int x, out int y)
    {
        x = y = 0;
        if (_frame.W == 0) return false;
        double W = _surface.ActualWidth, H = _surface.ActualHeight;
        double scale = Math.Min(W / _frame.W, H / _frame.H);
        double ox = (W - _frame.W * scale) / 2, oy = (H - _frame.H * scale) / 2;
        x = (int)Math.Clamp((p.X - ox) / scale, 0, _frame.W - 1);
        y = (int)Math.Clamp((p.Y - oy) / scale, 0, _frame.H - 1);
        return true;
    }

    private void OnPointer(PointerRoutedEventArgs e, ControlMessage.MotionAction action)
    {
        if (_session is null) return;
        var pt = e.GetCurrentPoint(_surface);
        var props = pt.Properties;
        if (action == ControlMessage.MotionAction.Down)
        {
            if (props.IsRightButtonPressed)
            {
                _session.Send(ControlMessage.BackOrScreenOn(ControlMessage.KeyAction.Down));
                _session.Send(ControlMessage.BackOrScreenOn(ControlMessage.KeyAction.Up));
                return;
            }
            if (props.IsMiddleButtonPressed) { TapKey(3); return; }
            if (!props.IsLeftButtonPressed) return;
            _pressed = true;
            _surface.CapturePointer(e.Pointer);
        }
        else if (!_pressed) return;
        if (action == ControlMessage.MotionAction.Up) { _pressed = false; _surface.ReleasePointerCapture(e.Pointer); }
        if (!ToPhone(pt.Position, out int x, out int y)) return;
        bool up = action == ControlMessage.MotionAction.Up;
        _session.Send(ControlMessage.Touch(action, ControlMessage.PointerFinger, x, y, (ushort)_frame.W, (ushort)_frame.H,
            up ? 0f : 1f, 0, up ? 0 : 1));
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_session is null) return;
        var pt = e.GetCurrentPoint(_surface);
        if (!ToPhone(pt.Position, out int x, out int y)) return;
        float v = pt.Properties.MouseWheelDelta / 120f;     // 向上滾＝內容往下
        _session.Send(ControlMessage.Scroll(x, y, (ushort)_frame.W, (ushort)_frame.H, 0, v));
        e.Handled = true;
    }

    /// <summary>實體按鍵：依按鍵位置送 Android keycode（文字輸入與輸入法之後再做）</summary>
    private void OnKey(KeyRoutedEventArgs e, ControlMessage.KeyAction action)
    {
        if (_session is null) return;
        int code = KeyMap(e.Key);
        if (code < 0) return;
        _session.Send(ControlMessage.Keycode(action, code));
        e.Handled = true;
    }

    private static int KeyMap(VirtualKey k)
    {
        if (k is >= VirtualKey.A and <= VirtualKey.Z) return 29 + (k - VirtualKey.A);
        if (k is >= VirtualKey.Number0 and <= VirtualKey.Number9) return 7 + (k - VirtualKey.Number0);
        return k switch
        {
            VirtualKey.Enter => 66, VirtualKey.Back => 67, VirtualKey.Delete => 112, VirtualKey.Tab => 61, VirtualKey.Space => 62,
            VirtualKey.Escape => 4, VirtualKey.Up => 19, VirtualKey.Down => 20, VirtualKey.Left => 21, VirtualKey.Right => 22,
            VirtualKey.Home => 122, VirtualKey.End => 123, VirtualKey.PageUp => 92, VirtualKey.PageDown => 93,
            _ => -1,
        };
    }
}
