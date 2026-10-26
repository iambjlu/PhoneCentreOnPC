using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace PhoneCentre.Support;

/// <summary>
/// 視窗訊息攔截（SetWindowSubclass）：WinUI 沒有提供的視窗行為——最小大小（WM_GETMINMAXINFO）、
/// 縮放時維持影像比例（WM_SIZING）、放開邊框（WM_EXITSIZEMOVE）。對應 MirrorWindowController 的 windowWillResize 等。
/// </summary>
public sealed class WindowSubclass : IDisposable
{
    private const uint WM_GETMINMAXINFO = 0x0024, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232, WM_DPICHANGED = 0x02E0;
    private const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4, WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MINMAXINFO { public POINT Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize; }

    private readonly IntPtr _hwnd;
    private readonly SubclassProc _proc;   // 要一直持有，不然會被 GC 回收
    private bool _disposed;

    /// <summary>最小大小（整個視窗，含標題列與邊框，單位是 DIP）。回傳 null＝不限制</summary>
    public Func<(double Width, double Height)?>? MinSize { get; set; }
    /// <summary>縮放時要維持的「客戶區」長寬比（寬/高）與固定多出來的客戶區寬、高（DIP，例如右側欄寬度）。回傳 null＝自由縮放</summary>
    public Func<(double Aspect, double ExtraWidth, double ExtraHeight)?>? KeepAspect { get; set; }
    /// <summary>攔截任意視窗訊息（例如系統匣圖示的回呼訊息）；回傳 true 表示已處理</summary>
    public Func<uint, IntPtr, IntPtr, bool>? MessageHook { get; set; }
    /// <summary>使用者放開邊框（縮放結束）</summary>
    public event Action? ResizeEnded;

    public WindowSubclass(Window window)
    {
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _proc = Proc;
        SetWindowSubclass(_hwnd, _proc, (UIntPtr)1, UIntPtr.Zero);
        window.Closed += (_, _) => Dispose();
    }

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        if (MessageHook?.Invoke(msg, wParam, lParam) == true) return IntPtr.Zero;
        switch (msg)
        {
            case WM_GETMINMAXINFO when MinSize?.Invoke() is { } min:
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                mmi.MinTrackSize.X = (int)Math.Ceiling(min.Width * Scale);
                mmi.MinTrackSize.Y = (int)Math.Ceiling(min.Height * Scale);
                Marshal.StructureToPtr(mmi, lParam, false);
                return IntPtr.Zero;
            }
            case WM_SIZING when KeepAspect?.Invoke() is { } k:
            {
                var r = Marshal.PtrToStructure<RECT>(lParam);
                GetWindowRect(hWnd, out var win); GetClientRect(hWnd, out var client);
                int chromeW = (win.Right - win.Left) - client.Right, chromeH = (win.Bottom - win.Top) - client.Bottom;
                double s = Scale;
                double extraW = k.ExtraWidth * s, extraH = k.ExtraHeight * s;
                // 客戶區 = 影像區（維持長寬比）＋固定的額外寬高
                double cw = (r.Right - r.Left) - chromeW, ch = (r.Bottom - r.Top) - chromeH;
                int edge = (int)wParam;
                bool horizontalDrag = edge is WMSZ_LEFT or WMSZ_RIGHT;
                double vw, vh;
                if (horizontalDrag) { vw = Math.Max(cw - extraW, 50); vh = vw / k.Aspect; }
                else { vh = Math.Max(ch - extraH, 50); vw = vh * k.Aspect; }
                // 不能小於最小大小：兩邊都放大到夠為止
                if (MinSize?.Invoke() is { } m)
                {
                    double minCw = m.Width * s - chromeW, minCh = m.Height * s - chromeH;
                    double scale = Math.Max(Math.Max((minCw - extraW) / vw, (minCh - extraH) / vh), 1);
                    vw *= scale; vh *= scale;
                }
                int newW = (int)Math.Round(vw + extraW) + chromeW, newH = (int)Math.Round(vh + extraH) + chromeH;
                // 依拖曳的邊決定哪一邊固定
                if (edge is WMSZ_LEFT or WMSZ_TOPLEFT or WMSZ_BOTTOMLEFT) r.Left = r.Right - newW; else r.Right = r.Left + newW;
                if (edge is WMSZ_TOP or WMSZ_TOPLEFT or WMSZ_TOPRIGHT) r.Top = r.Bottom - newH; else r.Bottom = r.Top + newH;
                Marshal.StructureToPtr(r, lParam, false);
                return (IntPtr)1;
            }
            case WM_EXITSIZEMOVE:
                ResizeEnded?.Invoke();
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveWindowSubclass(_hwnd, _proc, (UIntPtr)1);
    }
}
