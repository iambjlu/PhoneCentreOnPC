using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace PhoneCentre.Support;

/// <summary>系統匣圖示（對應 MenuBarController 的 NSStatusItem）。用 Shell_NotifyIcon，回呼訊息掛在主視窗的 <see cref="WindowSubclass"/> 上。</summary>
public sealed class TrayIcon : IDisposable
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint WM_APP_TRAY = 0x8000 + 1, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;

    private readonly IntPtr _hwnd;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private IntPtr _icon;
    private bool _visible;

    /// <summary>使用者點了圖示（左鍵或右鍵）</summary>
    public event Action? Clicked;

    public TrayIcon(Window window, WindowSubclass sub)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (WindowIcon.Path is { } p) _icon = LoadImage(IntPtr.Zero, p, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        sub.MessageHook = (msg, w, l) =>
        {
            if (msg == WM_APP_TRAY)
            {
                uint e = (uint)(l.ToInt64() & 0xFFFF);
                if (e is WM_LBUTTONUP or WM_RBUTTONUP) Clicked?.Invoke();
                return true;
            }
            if (msg == _taskbarCreated && _visible) { Add(); return false; }   // 檔案總管重新啟動後圖示會消失，要再加回去
            return false;
        };
    }

    private NOTIFYICONDATA Data() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(), hWnd = _hwnd, uID = 1, uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
        uCallbackMessage = WM_APP_TRAY, hIcon = _icon, szTip = "PhoneCentre", szInfo = "", szInfoTitle = "",
    };

    private void Add() { var d = Data(); Shell_NotifyIcon(NIM_ADD, ref d); }

    public void SetVisible(bool visible)
    {
        if (visible == _visible) return;
        _visible = visible;
        if (visible) Add(); else { var d = Data(); Shell_NotifyIcon(NIM_DELETE, ref d); }
    }

    public bool Visible => _visible;

    public void Dispose()
    {
        SetVisible(false);
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
    }
}
