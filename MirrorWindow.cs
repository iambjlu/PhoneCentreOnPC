using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Adb;
using PhoneCentre.Mirror;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.Graphics;

namespace PhoneCentre;

/// <summary>
/// 鏡像視窗（對應 MirrorWindowController.swift＋MirrorContentView.swift）：主螢幕鏡像依手機畫面比例調整；
/// 虛擬顯示器（獨立 App、桌面模式）可自由調整大小，放開邊框時依新的大小重建虛擬顯示器，App 會以新的長寬重新排版。
/// </summary>
public sealed class MirrorWindow : Window
{
    public MirrorModel Model { get; }
    private readonly MirrorView _view;
    private readonly WindowSubclass _sub;
    private readonly Grid _root = new();
    private readonly Grid _videoArea = new() { Background = Ui.Ui.Hex(0xFF000000) };
    private readonly Grid _overlays = new() { IsHitTestVisible = true };
    private readonly Rectangle _flash = new() { Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Opacity = 0, IsHitTestVisible = false };
    private SideColumn _rail;
    private bool _compact, _sizedOnce, _userResized, _closing, _cleanedUp;
    private (double W, double H)? _savedVideo;
    private double? _savedMainVideoHeight;
    public Action? OnClosed;

    private double Scale => Content?.XamlRoot?.RasterizationScale ?? 1;

    public MirrorWindow(MirrorModel model, (double X, double Y)? topLeft = null)
    {
        Log.Write("MW:ctor start");
        Model = model;
        Title = model.Title;
        PhoneCentre.Support.WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();

        Log.Write("MW:new MirrorView");
        _view = new MirrorView(model, this);
        if (model.IsVirtualDisplay)
        {
            _view.OnBack = model.NavBack;
            // 中鍵／Ctrl＋Backspace：桌面模式送 Home 到該桌面；單一 App 視窗不要因為誤按中鍵就關掉視窗
            if (model.DesktopMode) _view.OnHome = model.NavHome;
        }
        Log.Write("MW:new SideColumn");
        _rail = new SideColumn(model, this);

        Log.Write("MW:layout");
        _videoArea.Children.Add(_view);
        _videoArea.Children.Add(_overlays);
        _videoArea.Children.Add(_flash);
        Content = _root;
        LayoutRoot(false);

        Log.Write("MW:subclass");
        _sub = new WindowSubclass(this)
        {
            MinSize = () => MinFrame(IsLandscape()),
            KeepAspect = () => !Model.IsVirtualDisplay && Model.FrameSize.W > 0 && Model.FrameSize.H > 0 ? (Model.FrameSize.W / (double)Model.FrameSize.H, _compact ? 0 : MirrorLayout.RailWidth, _compact ? MirrorLayout.BottomBarHeight : MirrorLayout.ToolbarHeight) : null,
        };
        _sub.ResizeEnded += OnResizeEnded;

        Log.Write("MW:hooks");
        model.Changed += Refresh;
        model.OnFrameSize = Fit;
        model.OnRotateWindow = RotateWindow;
        model.OnCloseWindow = () => Close();
        model.OnHideWindow = () => AppWindow.Hide();
        model.GetXamlRoot = () => Content.XamlRoot;
        model.IsActive = () => AppWindow.IsVisible && IsForeground;
        model.FlashRequested += Flash;
        model.LinkChanged += () => { };
        model.Catalog.Changed += () => { Title = model.Title; };
        Closed += OnWindowClosed;
        // 關閉前先讓 App 回到背景（虛擬顯示器視窗）
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += (_, e) => { if (e.DidSizeChange) OnSizeChanged(); };

        // 還原上次的視窗大小（獨立視窗常見）
        Log.Write("MW:restore");
        RestoreFrame(topLeft);
        Log.Write("MW:refresh");
        Refresh();
        Log.Write("MW:ctor done");
    }

    private bool IsForeground => GetForegroundWindow() == WinRT.Interop.WindowNative.GetWindowHandle(this);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    // MARK: 版面（右側欄在右邊，或橫向且高度不夠時移到底部）

    private (double W, double H) ContentSize
    {
        get { var c = AppWindow.ClientSize; double s = Scale; return (c.Width / s, c.Height / s); }
    }

    private bool IsLandscape()
    {
        var (fw, fh) = Model.FrameSize;
        if (!Model.IsVirtualDisplay && fw > 0 && fh > 0) return fw > fh;
        var c = ContentSize;
        return c.W > c.H;
    }

    private static (double W, double H) MinFrame(bool landscape) => landscape
        ? (MirrorLayout.LandscapeMinFrameWidth, MirrorLayout.LandscapeMinFrameHeight)
        : (MirrorLayout.PortraitMinFrameWidth, MirrorLayout.PortraitMinFrameHeight);

    private bool ShouldCompact()
    {
        var c = ContentSize;
        bool landscape = Model.IsVirtualDisplay ? c.W > c.H : Model.FrameSize.W > Model.FrameSize.H;
        return landscape && c.H < MirrorLayout.CompactContentHeight;
    }

    private double ChromeW => _compact ? 0 : MirrorLayout.RailWidth;
    private double ChromeH => _compact ? MirrorLayout.BottomBarHeight : MirrorLayout.ToolbarHeight;

    /// <summary>橫向模式且視窗內容高度未滿 608：右側欄整排移到視窗底部；直向模式永遠在右邊</summary>
    private void LayoutRoot(bool compact)
    {
        _compact = compact;
        _root.Children.Clear(); _root.ColumnDefinitions.Clear(); _root.RowDefinitions.Clear();
        _rail.SetHorizontal(compact);
        if (compact)
        {
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MirrorLayout.BottomBarHeight) });
            _root.Children.Add(_videoArea);
            Grid.SetRow(_rail, 1); _root.Children.Add(_rail);
        }
        else
        {
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(MirrorLayout.RailWidth) });
            _root.Children.Add(_videoArea);
            Grid.SetColumn(_rail, 1); _root.Children.Add(_rail);
        }
    }

    private void OnSizeChanged()
    {
        bool compact = ShouldCompact();
        if (compact != _compact) LayoutRoot(compact);
        ClampToMin();
        SaveFrame();
    }

    // MARK: 視窗大小

    private string FrameKey => $"Mirror.{Model.Device.Id}.{Model.AppPackage ?? (Model.DesktopMode ? "desktop" : "main")}";

    private void RestoreFrame((double X, double Y)? topLeft)
    {
        var area = DisplayArea.Primary.WorkArea;
        bool restored = false;
        try
        {
            if (AppSettings.Data.WindowFrames.TryGetValue(FrameKey, out var f) && f.Length == 4 && f[2] > 100 && f[3] > 100)
            {
                // f = 客戶區寬、高（DIP）、左上角 x、y（像素）
                double s = Scale;
                AppWindow.ResizeClient(new SizeInt32((int)(f[2] * s), (int)(f[3] * s)));
                var pos = new PointInt32((int)f[0], (int)f[1]);
                if (pos.X > area.X - 200 && pos.X < area.X + area.Width - 100 && pos.Y > area.Y - 20 && pos.Y < area.Y + area.Height - 100) AppWindow.Move(pos);
                restored = true;
                var (cw, ch) = (f[2], f[3]);
                if (Model.IsVirtualDisplay) _savedVideo = (cw - MirrorLayout.RailWidth, ch - MirrorLayout.ToolbarHeight);
                else _savedMainVideoHeight = ch - MirrorLayout.ToolbarHeight;
            }
        }
        catch { }
        if (!restored) AppWindow.Resize(new SizeInt32(Math.Min(520, area.Width), Math.Min(900, area.Height)));
        if (topLeft is { } p) AppWindow.Move(new PointInt32((int)p.X, (int)p.Y));
        else if (!restored)
        {
            // 錯開排列，不要疊在其他視窗正上方
            int n = WindowManager.OpenCount;
            AppWindow.Move(new PointInt32(area.X + 80 + 40 * n, area.Y + 40 + 30 * n));
        }
    }

    private void SaveFrame()
    {
        if (!_sizedOnce || _closing) return;
        try
        {
            var (cw, ch) = ContentSize; var p = AppWindow.Position;
            AppSettings.Data.WindowFrames[FrameKey] = [p.X, p.Y, cw, ch];
        }
        catch { }
    }

    /// <summary>依影像大小調整視窗（第一次或旋轉時）</summary>
    private void Fit(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        Title = Model.Title;
        // 虛擬顯示器的大小是依視窗決定的，使用者調整過後就不要反過來改視窗
        if (Model.IsVirtualDisplay && _userResized) { _sizedOnce = true; return; }
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double s = Scale, sw = area.Width / s, sh = area.Height / s;
        var (cw, ch) = ContentSize;
        double aspect = w / (double)h, videoH;
        if (_sizedOnce)
        {
            // 旋轉：保持較長邊大約不變
            var oldVideo = (W: cw - ChromeW, H: ch - ChromeH);
            double longSide = Math.Max(oldVideo.W, oldVideo.H);
            videoH = aspect >= 1 ? longSide / aspect : longSide;
        }
        else
        {
            videoH = Model.IsVirtualDisplay ? Math.Min(h / Math.Max(1, s), sh * 0.9 - ChromeH) : Math.Min(sh * 0.85 - ChromeH - 40, h);
            if (!Model.IsVirtualDisplay && _savedMainVideoHeight is { } sv && sv > 200) videoH = Math.Min(sv, sh * 0.95 - ChromeH - 40);
        }
        double videoW = videoH * aspect, maxW = sw * 0.9 - ChromeW;
        if (videoW > maxW) { videoW = maxW; videoH = videoW / aspect; }
        double contentW = Math.Round(videoW + MirrorLayout.RailWidth), contentH = Math.Round(videoH + MirrorLayout.ToolbarHeight);
        // 高度不到門檻：右側欄會移到底部橫條，影像區是整個寬度、下面多一條橫條
        if (w > h && contentH < MirrorLayout.CompactContentHeight) { contentW = Math.Round(videoW); contentH = Math.Round(videoH + MirrorLayout.BottomBarHeight); }
        AppWindow.ResizeClient(new SizeInt32((int)(contentW * s), (int)(contentH * s)));
        // 不要超出螢幕
        var pos = AppWindow.Position; var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(Math.Clamp(pos.X, area.X, Math.Max(area.X, area.X + area.Width - size.Width)), Math.Clamp(pos.Y, area.Y, Math.Max(area.Y, area.Y + area.Height - size.Height))));
        _sizedOnce = true;
        OnSizeChanged();
    }

    /// <summary>視窗比最小大小還小就放大（從上次記住的大小還原時不會自動套用最小值）。左上角不動。</summary>
    private void ClampToMin()
    {
        var (mw, mh) = MinFrame(IsLandscape());
        double s = Scale;
        var size = AppWindow.Size;
        int minW = (int)Math.Ceiling(mw * s), minH = (int)Math.Ceiling(mh * s);
        if (size.Width >= minW - 1 && size.Height >= minH - 1) return;
        AppWindow.Resize(new SizeInt32(Math.Max(size.Width, minW), Math.Max(size.Height, minH)));
    }

    /// <summary>虛擬顯示器旋轉：視窗寬高對調，再依新的大小重建顯示器</summary>
    private void RotateWindow()
    {
        var (cw, ch) = ContentSize;
        var video = (W: cw - ChromeW, H: ch - ChromeH);
        double s = Scale;
        AppWindow.ResizeClient(new SizeInt32((int)((video.H + ChromeW) * s), (int)((video.W + ChromeH) * s)));
        _userResized = true;
        Model.BackingScale = s;
        var n = ContentSize;
        Model.ResizeVirtualDisplay(n.W - ChromeW, n.H - ChromeH);
    }

    /// <summary>放開邊框：依新的視窗大小重建虛擬顯示器</summary>
    private void OnResizeEnded()
    {
        if (!Model.IsVirtualDisplay) return;
        _userResized = true;
        Model.BackingScale = Scale;
        var c = ContentSize;
        Model.ResizeVirtualDisplay(c.W - ChromeW, c.H - ChromeH);
    }

    /// <summary>顯示視窗並連線（對應 show()）</summary>
    public new void Activate()
    {
        Model.BackingScale = Scale;
        if (_savedVideo is { } sv && sv.W > 100 && sv.H > 100) { _userResized = true; Model.PresetDisplay(sv.W, sv.H); }
        Log.Write("MW:activate");
        base.Activate();
        AppWindow.Show();
        Log.Write("MW:activated");
        _view.Focus(FocusState.Programmatic);
        if (!_started) { _started = true; Log.Write("MW:model start"); Model.Start(); Log.Write("MW:model started"); }
    }

    private bool _started;

    // MARK: 關閉

    private async void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        if (_cleanedUp || !Model.IsVirtualDisplay) return;
        // 虛擬顯示器視窗關閉前，先讓 App 回到背景再真正關閉
        e.Cancel = true;
        await Model.SendAppsToBackgroundAsync();
        _cleanedUp = true;
        Close();
    }

    private void OnWindowClosed(object sender, WindowEventArgs e)
    {
        _closing = true;
        try { AppSettings.Save(); } catch { }
        Model.Changed -= Refresh;
        _view.Detach();
        Model.Stop();
        OnClosed?.Invoke();
    }

    // MARK: 疊加訊息（一律毛玻璃底）

    private static Border GlassCard(UIElement child, double radius = 14) => new()
    {
        Child = child, CornerRadius = new CornerRadius(radius), Background = Ui.Ui.Hex(0xD8202225),
        BorderBrush = Ui.Ui.Hex(0x30FFFFFF), BorderThickness = new Thickness(1),
    };

    private void Flash()
    {
        _flash.Opacity = 1;
        var anim = new DoubleAnimation { From = 1, To = 0, Duration = TimeSpan.FromMilliseconds(500), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(anim, _flash); Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard(); sb.Children.Add(anim); sb.Begin();
    }

    /// <summary>依 Model 的狀態重畫疊加層與右側欄</summary>
    private void Refresh()
    {
        Title = Model.Title;
        _overlays.Children.Clear();

        // 主螢幕與獨立視窗都用頂端的小橫幅（可以關掉）：手機鎖著時第二螢幕常常還看得到 App，不要用大卡片擋住畫面
        if (Model.ShowUnlockBanner)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(2) };
            row.Children.Add(new MIcon("lock", 18));
            row.Children.Add(new TextBlock { Text = "手機已鎖定", VerticalAlignment = VerticalAlignment.Center });
            if (LockScreenHelper.HasPassword(Model.Device))
            {
                var b = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
                b.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new MIcon("fingerprint", 16), new TextBlock { Text = "用 Windows Hello 解鎖" } } };
                b.Click += async (_, _) => await Model.UnlockAsync();
                row.Children.Add(b);
            }
            else
            {
                var b = new Button { Content = "儲存解鎖密碼…" };
                b.Click += async (_, _) => await Popovers.ShowPasswordDialogAsync(Model.Device, Content.XamlRoot);
                row.Children.Add(b);
            }
            var x = new Button { Content = new MIcon("close", 14), Padding = new Thickness(6), Background = null, BorderThickness = new Thickness(0) };
            x.Click += (_, _) => Model.DismissUnlock();
            row.Children.Add(x);
            var card = GlassCard(row, 12); card.Padding = new Thickness(10);
            card.VerticalAlignment = VerticalAlignment.Top; card.HorizontalAlignment = HorizontalAlignment.Center;
            card.Margin = new Thickness(0, Model.IsVirtualDisplay ? 12 : 49, 0, 0);   // 主螢幕往下一點，不要擋住手機的狀態列
            _overlays.Children.Add(card);
        }

        if (Model.MarkedText.Length > 0)
        {
            var t = new TextBlock { Text = Model.MarkedText, FontSize = 18 };
            var card = GlassCard(t, 16); card.Padding = new Thickness(14, 7, 14, 7);
            card.VerticalAlignment = VerticalAlignment.Bottom; card.HorizontalAlignment = HorizontalAlignment.Center; card.Margin = new Thickness(0, 0, 0, 100);
            card.IsHitTestVisible = false;
            _overlays.Children.Add(card);
        }

        if (Model.AppClosed && Model.StatusText is null && Model.AppPackage is { } pkg)
        {
            // 整個畫面區毛玻璃：最後一幀霧化當底，上面再蓋一層毛玻璃（App 不支援橫向時兩側的黑邊也一起霧化）
            if (Model.ClosedSnapshot is { } snap)
            {
                var bmp = new WriteableBitmap(snap.W, snap.H);
                using (var st = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsStream(bmp.PixelBuffer)) st.Write(snap.Bgra, 0, snap.W * snap.H * 4);
                bmp.Invalidate();
                _overlays.Children.Add(new Image { Source = bmp, Stretch = Stretch.UniformToFill, IsHitTestVisible = false });
            }
            _overlays.Children.Add(new Rectangle
            {
                IsHitTestVisible = false,
                Fill = new AcrylicBrush { TintColor = Windows.UI.Color.FromArgb(255, 24, 26, 30), TintOpacity = 0.55, TintLuminosityOpacity = 0.7, FallbackColor = Windows.UI.Color.FromArgb(235, 24, 26, 30) },
            });
            var col = new StackPanel { Spacing = 12, Padding = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Center };
            var icon = Ui.Ui.AppIcon(Model.Catalog, pkg, 56); icon.Opacity = 0.9; icon.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(icon);
            col.Children.Add(new TextBlock { Text = $"{Model.AppLabel ?? Model.Catalog.Label(pkg)} 已關閉", FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var open = new Button { Content = "打開", Style = (Style)Application.Current.Resources["AccentButtonStyle"], Padding = new Thickness(28, 8, 28, 8), HorizontalAlignment = HorizontalAlignment.Center };
            open.Click += (_, _) => Model.ReopenApp();
            var close = new Button { Content = "關閉視窗", Background = null, BorderThickness = new Thickness(0), Foreground = Ui.Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Center };
            close.Click += (_, _) => Model.OnCloseWindow?.Invoke();
            col.Children.Add(open); col.Children.Add(close);
            var card = GlassCard(col, 18); card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            _overlays.Children.Add(card);
        }

        if (Model.StatusText is { } status)
        {
            var col = new StackPanel { Spacing = 12, Padding = new Thickness(20), HorizontalAlignment = HorizontalAlignment.Center };
            col.Children.Add(new ProgressRing { IsActive = true, Width = 22, Height = 22 });
            col.Children.Add(new TextBlock { Text = status, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 });
            var card = GlassCard(col, 16); card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center;
            _overlays.Children.Add(card);
        }

        // 提示訊息顯示在畫面正中間
        if (Model.Toast is { } toast)
        {
            var t = new TextBlock { Text = toast, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
            var card = GlassCard(t, 14); card.Padding = new Thickness(16, 10, 16, 10); card.Margin = new Thickness(24);
            card.HorizontalAlignment = HorizontalAlignment.Center; card.VerticalAlignment = VerticalAlignment.Center; card.IsHitTestVisible = false;
            _overlays.Children.Add(card);
        }
        _rail.Update();
    }
}
