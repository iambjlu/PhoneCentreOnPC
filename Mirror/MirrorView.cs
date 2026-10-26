using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.Core;

namespace PhoneCentre.Mirror;

/// <summary>顯示手機畫面並把滑鼠／觸控板／鍵盤事件轉成 scrcpy 控制訊息（對應 MirrorView.swift）</summary>
public sealed class MirrorView : UserControl
{
    private readonly MirrorModel _model;
    private readonly Window _window;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private WriteableBitmap? _bitmap;

    // 觸控狀態
    private bool _fingerDown;
    // 觸控板捲動 → 模擬手指拖曳
    private bool _scrolling;
    private (double X, double Y) _scrollAnchor, _scrollAccum, _scrollPos;
    private bool _scrollFingerDown;
    private DispatcherQueueTimer? _scrollEnd, _pinchEnd;
    // 雙指縮放（Ctrl＋滾輪／觸控板捏合）
    private bool _pinching;
    private (double X, double Y) _pinchCenter;
    private double _pinchScale = 1, _pinchBase;
    private const ulong PinchA = 0x5043_0001, PinchB = 0x5043_0002, ScrollId = 0x5043_0003;

    /// <summary>虛擬顯示器的視窗：返回／主畫面要作用在該顯示器上，由 MirrorModel 接手（null = 直接送 scrcpy）</summary>
    public Action? OnBack, OnHome;

    // 輸入法：畫面上放一個看不見的 TextBox 當橋接（組字與候選窗由 WinUI 的 TextBox 原生處理，這裡只讀出組字與提交的文字）
    private readonly TextBox _imeBox = new()
    {
        Width = 220, Height = 30, Opacity = 0.01, IsHitTestVisible = false, BorderThickness = new Thickness(0), Background = null,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 64),
        AcceptsReturn = false, IsSpellCheckEnabled = false, IsTextPredictionEnabled = false,
    };
    private bool _composing;

    public (int W, int H) VideoSize => _model.FrameSize;

    public MirrorView(MirrorModel model, Window window)
    {
        _model = model; _window = window;
        IsTabStop = true;
        Background = Ui.Ui.Hex(0xFF000000);
        Content = new Grid { Background = Ui.Ui.Hex(0xFF000000), Children = { _image, _imeBox } };
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
        AllowDrop = true;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCanceled += (_, e) => { if (_fingerDown) EndFinger(e); };
        PointerWheelChanged += OnWheel;
        PreviewKeyDown += OnKeyDown;
        PreviewKeyUp += OnKeyUp;
        GotFocus += (_, e) => { if (_model.KeyboardMode == KeyboardMode.Ime && !ReferenceEquals(e.OriginalSource, _imeBox)) _imeBox.Focus(FocusState.Programmatic); };
        _imeBox.LostFocus += (_, _) => CancelComposition();
        _imeBox.TextCompositionStarted += (_, _) => _composing = true;
        _imeBox.TextCompositionChanged += (tb, a) =>
        {
            var t = tb.Text; int st = Math.Clamp(a.StartIndex, 0, t.Length), len = Math.Clamp(a.Length, 0, t.Length - st);
            _model.MarkedText = t.Substring(st, len); _model.ShowMarked();
        };
        _imeBox.TextCompositionEnded += (_, _) => { _composing = false; CommitBuffer(); };
        _imeBox.TextChanged += (_, _) => { if (!_composing) CommitBuffer(); };
        DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count > 0) _model.Drop(items.Select(i => i.Path).ToList());
        };

        var q = DispatcherQueue.GetForCurrentThread();
        _scrollEnd = q.CreateTimer(); _scrollEnd.IsRepeating = false; _scrollEnd.Interval = TimeSpan.FromMilliseconds(110);
        _scrollEnd.Tick += (_, _) => EndTouchpadScroll();
        _pinchEnd = q.CreateTimer(); _pinchEnd.IsRepeating = false; _pinchEnd.Interval = TimeSpan.FromMilliseconds(180);
        _pinchEnd.Tick += (_, _) => EndPinch();
        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
    }

    public void Detach() => CompositionTarget.Rendering -= OnRendering;

    /// <summary>清掉目前顯示的畫面（重新啟動時）</summary>
    public void Clear() { _image.Source = null; _bitmap = null; }

    // MARK: 畫面

    /// <summary>每個畫面刷新取目前解碼器的最新幀，寫進 WriteableBitmap</summary>
    private void OnRendering(object? sender, object e)
    {
        var dec = _model.Decoder;
        if (dec is null) return;
        dec.TryTakeFrame((bgra, w, h) =>
        {
            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h);
                _image.Source = _bitmap;
            }
            using var stream = _bitmap.PixelBuffer.AsStream();
            stream.Write(bgra, 0, w * h * 4);
            _bitmap.Invalidate();
        });
    }

    // MARK: 座標

    /// <summary>影像實際顯示區域（aspect fit）</summary>
    private Rect VideoRect
    {
        get
        {
            var (fw, fh) = _model.FrameSize;
            double W = ActualWidth, H = ActualHeight;
            if (fw <= 0 || fh <= 0) return new Rect(0, 0, W, H);
            double s = Math.Min(W / fw, H / fh);
            double w = fw * s, h = fh * s;
            return new Rect((W - w) / 2, (H - h) / 2, w, h);
        }
    }

    /// <summary>view 座標 → 裝置畫面座標</summary>
    private (double X, double Y) DevicePoint(Point p)
    {
        var r = VideoRect; var (fw, fh) = _model.FrameSize;
        double x = (p.X - r.X) / Math.Max(r.Width, 1) * fw, y = (p.Y - r.Y) / Math.Max(r.Height, 1) * fh;
        return (Math.Clamp(x, 0, fw - 1), Math.Clamp(y, 0, fh - 1));
    }

    private double ViewToDeviceScale => VideoRect.Width > 0 ? _model.FrameSize.W / VideoRect.Width : 1;

    private void Touch(ControlMessage.MotionAction action, (double X, double Y) p, ulong pointer = ControlMessage.PointerFinger)
    {
        var (fw, fh) = _model.FrameSize;
        if (fw <= 0) return;
        _model.Session?.Send(ControlMessage.Touch(action, pointer, (int)p.X, (int)p.Y, (ushort)fw, (ushort)fh, action == ControlMessage.MotionAction.Up ? 0f : 1f));
    }

    // MARK: 滑鼠

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_model.KeyboardMode == KeyboardMode.Ime) _imeBox.Focus(FocusState.Pointer); else Focus(FocusState.Pointer);
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        if (props.IsRightButtonPressed)       // 右鍵 = 返回（螢幕關閉時則點亮）
        {
            if (OnBack is not null) OnBack(); else _model.Session?.Send(ControlMessage.BackOrScreenOn(ControlMessage.KeyAction.Down));
            return;
        }
        if (props.IsMiddleButtonPressed)      // 中鍵 = 主畫面
        {
            if (OnHome is not null) OnHome(); else SendKey(KeyMap.Home, ControlMessage.KeyAction.Down);
            return;
        }
        if (!props.IsLeftButtonPressed) return;
        _fingerDown = true;
        CapturePointer(e.Pointer);
        Touch(ControlMessage.MotionAction.Down, DevicePoint(pt.Position));
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_fingerDown) return;
        Touch(ControlMessage.MotionAction.Move, DevicePoint(e.GetCurrentPoint(this).Position));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_fingerDown) { EndFinger(e); return; }
        // 右鍵、中鍵放開
        var changed = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (changed == PointerUpdateKind.RightButtonReleased && OnBack is null) _model.Session?.Send(ControlMessage.BackOrScreenOn(ControlMessage.KeyAction.Up));
        if (changed == PointerUpdateKind.MiddleButtonReleased && OnHome is null) SendKey(KeyMap.Home, ControlMessage.KeyAction.Up);
    }

    private void EndFinger(PointerRoutedEventArgs e)
    {
        _fingerDown = false;
        ReleasePointerCapture(e.Pointer);
        Touch(ControlMessage.MotionAction.Up, DevicePoint(e.GetCurrentPoint(this).Position));
    }

    // MARK: 滾輪、觸控板捲動、雙指縮放

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        var props = pt.Properties;
        var p = DevicePoint(pt.Position);
        e.Handled = true;
        bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl) { Pinch(props.MouseWheelDelta, p); return; }          // 觸控板捏合會變成 Ctrl＋滾輪
        var (fw, fh) = _model.FrameSize;
        if (fw <= 0) return;
        // 觸控板：模擬單指拖曳，讓 Android 自己算慣性，手感跟手機一樣。一般滑鼠滾輪：送捲動事件
        if (e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touchpad)
        {
            double dx = props.IsHorizontalMouseWheel ? props.MouseWheelDelta : 0, dy = props.IsHorizontalMouseWheel ? 0 : props.MouseWheelDelta;
            TouchpadScroll(p, dx, dy);
            return;
        }
        float h = props.IsHorizontalMouseWheel ? props.MouseWheelDelta / 120f : 0, v = props.IsHorizontalMouseWheel ? 0 : props.MouseWheelDelta / 120f;
        _model.Session?.Send(ControlMessage.Scroll((int)p.X, (int)p.Y, (ushort)fw, (ushort)fh, h, v));
    }

    private void TouchpadScroll((double X, double Y) p, double dxRaw, double dyRaw)
    {
        double s = ViewToDeviceScale;
        if (!_scrolling) { _scrolling = true; _scrollAnchor = p; _scrollAccum = (0, 0); _scrollFingerDown = false; }
        _scrollAccum = (_scrollAccum.X + dxRaw * s, _scrollAccum.Y + dyRaw * s);   // 向上滾（正值）＝內容往下，所以手指往下
        var (fw, fh) = _model.FrameSize;
        _scrollPos = (Math.Clamp(_scrollAnchor.X + _scrollAccum.X, 0, fw - 1), Math.Clamp(_scrollAnchor.Y + _scrollAccum.Y, 0, fh - 1));
        if (!_scrollFingerDown)
        {
            // 移動夠多才放下手指，避免誤觸成點擊
            if (Math.Sqrt(_scrollAccum.X * _scrollAccum.X + _scrollAccum.Y * _scrollAccum.Y) <= 6 * s) { _scrollEnd!.Stop(); _scrollEnd.Start(); return; }
            Touch(ControlMessage.MotionAction.Down, _scrollAnchor, ScrollId);
            _scrollFingerDown = true;
        }
        Touch(ControlMessage.MotionAction.Move, _scrollPos, ScrollId);
        _scrollEnd!.Stop(); _scrollEnd.Start();
    }

    private void EndTouchpadScroll()
    {
        if (_scrollFingerDown) Touch(ControlMessage.MotionAction.Up, _scrollPos, ScrollId);
        _scrolling = false; _scrollFingerDown = false;
    }

    private void Pinch(int delta, (double X, double Y) p)
    {
        var (fw, fh) = _model.FrameSize;
        if (fw <= 0) return;
        if (!_pinching)
        {
            _pinching = true; _pinchCenter = p; _pinchScale = 1;
            _pinchBase = Math.Max(Math.Min(fw, fh) * 0.12, 60);
            var (a, b) = PinchPoints();
            Touch(ControlMessage.MotionAction.Down, a, PinchA); Touch(ControlMessage.MotionAction.Down, b, PinchB);
        }
        _pinchScale = Math.Max(0.1, _pinchScale + delta / 1200.0);
        var (a2, b2) = PinchPoints();
        Touch(ControlMessage.MotionAction.Move, a2, PinchA); Touch(ControlMessage.MotionAction.Move, b2, PinchB);
        _pinchEnd!.Stop(); _pinchEnd.Start();
    }

    private void EndPinch()
    {
        if (!_pinching) return;
        var (a, b) = PinchPoints();
        Touch(ControlMessage.MotionAction.Up, b, PinchB); Touch(ControlMessage.MotionAction.Up, a, PinchA);
        _pinching = false;
    }

    /// <summary>斜向擺放兩指，靠近邊緣時也比較不會被擠壓</summary>
    private ((double, double), (double, double)) PinchPoints()
    {
        var (fw, fh) = _model.FrameSize;
        double half = _pinchBase * _pinchScale / 2, d = half * 0.7071;
        double Cx(double x) => Math.Clamp(x, 0, fw - 1);
        double Cy(double y) => Math.Clamp(y, 0, fh - 1);
        return ((Cx(_pinchCenter.X - d), Cy(_pinchCenter.Y - d)), (Cx(_pinchCenter.X + d), Cy(_pinchCenter.Y + d)));
    }

    // MARK: 鍵盤

    private void SendKey(int code, ControlMessage.KeyAction action, int meta = 0, int repeat = 0) =>
        _model.Session?.Send(ControlMessage.Keycode(action, code, repeat, meta));

    private void TapKey(int code, int meta = 0) { SendKey(code, ControlMessage.KeyAction.Down, meta); SendKey(code, ControlMessage.KeyAction.Up, meta); }

    private static bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);
    private static int CurrentMeta() => KeyMap.Meta(Down(VirtualKey.Shift), Down(VirtualKey.Menu), Down(VirtualKey.Control),
        InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.CapitalLock).HasFlag(CoreVirtualKeyStates.Locked));

    /// <summary>Ctrl 快捷鍵：剪貼簿、全選、復原等（Mac 版的 ⌘）</summary>
    private bool HandleShortcut(VirtualKey k)
    {
        const int ctrl = 0x1000 | 0x2000;
        switch (k)
        {
            case VirtualKey.V: _ = PasteAsync(); return true;
            case VirtualKey.C: _model.Session?.Send(ControlMessage.GetClipboard(1)); return true;
            case VirtualKey.X: _model.Session?.Send(ControlMessage.GetClipboard(2)); return true;
            case VirtualKey.A: TapKey(29, ctrl); return true;
            case VirtualKey.Z: TapKey(54, Down(VirtualKey.Shift) ? ctrl | 0x41 : ctrl); return true;
            default: return false;
        }
    }

    private async Task PasteAsync()
    {
        try
        {
            var c = Clipboard.GetContent();
            if (c.Contains(StandardDataFormats.Text)) _model.Session?.SetClipboard(await c.GetTextAsync(), true);
        }
        catch { }
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var k = e.Key;
        bool ctrl = Down(VirtualKey.Control), alt = Down(VirtualKey.Menu);
        if (ctrl && !alt && HandleShortcut(k)) { e.Handled = true; return; }
        var code = KeyMap.ForKey(k);
        if (code is null) return;
        // 輸入法模式：文字（字母、數字、符號）由輸入法組字後送出，只有控制鍵直接送 keycode；Ctrl 組合鍵直接送實體鍵
        if (_model.KeyboardMode == KeyboardMode.Ime && !ctrl && KeyMap.IsTextKey(k)) return;   // 文字交給輸入法（TextBox）
        if (_composing) return;                                                                 // 組字中的控制鍵（Enter、方向鍵…）是輸入法的
        SendKey(code.Value, ControlMessage.KeyAction.Down, CurrentMeta(), e.KeyStatus.WasKeyDown ? 1 : 0);
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyRoutedEventArgs e)
    {
        var k = e.Key;
        bool ctrl = Down(VirtualKey.Control);
        if (ctrl && k is VirtualKey.V or VirtualKey.C or VirtualKey.X or VirtualKey.A or VirtualKey.Z) return;
        var code = KeyMap.ForKey(k);
        if (code is null) return;
        if (_model.KeyboardMode == KeyboardMode.Ime && !ctrl && KeyMap.IsTextKey(k)) return;
        SendKey(code.Value, ControlMessage.KeyAction.Up, CurrentMeta());
        e.Handled = true;
    }

    // MARK: 輸入法

    private void NotifyModel() => _model.ShowMarked();

    private void CancelComposition()
    {
        _composing = false;
        _imeBox.Text = "";
        _model.MarkedText = ""; NotifyModel();
    }

    /// <summary>輸入法確定送出的文字：清掉緩衝並送到手機</summary>
    private void CommitBuffer()
    {
        var text = _imeBox.Text;
        if (text.Length == 0) return;
        _imeBox.Text = "";
        _model.MarkedText = ""; NotifyModel();
        _ = InjectTextAsync(text);
    }

    /// <summary>ASCII 用 INJECT_TEXT；中文等非 ASCII 透過剪貼簿貼上（Android 的 KeyCharacterMap 無法直接注入）。貼上前先記住電腦目前的剪貼簿，貼完再放回手機。</summary>
    private async Task InjectTextAsync(string s)
    {
        var session = _model.Session;
        if (session is null) return;
        if (s.All(c => c < 128)) { session.Send(ControlMessage.Text(s)); return; }
        string? saved = null;
        try { var c = Clipboard.GetContent(); if (c.Contains(StandardDataFormats.Text)) saved = await c.GetTextAsync(); } catch { }
        _model.ClipboardGuardUntil = DateTime.Now.AddSeconds(2);
        session.SetClipboard(s, true);
        await Task.Delay(800);
        if (saved is not null) session.SetClipboard(saved, false);
        _model.ClipboardGuardUntil = DateTime.Now.AddSeconds(0.8);
    }
}
