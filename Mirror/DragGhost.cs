using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace PhoneCentre.Mirror;

/// <summary>拖曳 App 圖示時跟著游標的半透明圖示（可以拖到視窗外面；對應 DragGhost）。一個透明、置頂、不搶焦點、滑鼠穿透的小視窗。</summary>
internal sealed class DragGhost
{
    public static DragGhost Shared { get; } = new();

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_LAYERED = 0x80000;

    private Window? _window;
    private readonly Image _image = new() { Width = 52, Height = 52, Margin = new Thickness(6) };
    private readonly Border _badge = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 48, 209, 88)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };

    public static POINT CursorPosition { get { GetCursorPos(out var p); return p; } }

    private void Ensure()
    {
        if (_window is not null) return;
        _badge.Child = new Ui.MIcon("add", 14) { Foreground = new SolidColorBrush(Microsoft.UI.Colors.White), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _window = new Window { Content = new Grid { Children = { _image, _badge } } };
        _window.SystemBackdrop = new DesktopAcrylicBackdrop();   // 半透明毛玻璃的小方塊（WinUI 沒有現成的全透明視窗）
        var p = Microsoft.UI.Windowing.OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false); p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        _window.AppWindow.SetPresenter(p);
        _window.AppWindow.IsShownInSwitchers = false;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)(ex | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_LAYERED));
    }

    public void Show(ImageSource? icon, bool armed, double scale)
    {
        Ensure();
        _image.Source = icon;
        _badge.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
        _window!.Content.Opacity = armed ? 0.95 : 0.6;
        var c = CursorPosition;
        int size = (int)(64 * scale);
        _window.AppWindow.MoveAndResize(new RectInt32(c.X - size / 2, c.Y - size / 2, size, size));
        _window.AppWindow.Show(false);
    }

    public void Hide() => _window?.AppWindow.Hide();
}
