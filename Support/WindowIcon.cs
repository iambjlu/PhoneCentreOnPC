using Microsoft.UI.Xaml;

namespace PhoneCentre.Support;

/// <summary>視窗與工作列圖示（內嵌的 AppIcon.ico，解到本機資料夾再交給 AppWindow）</summary>
public static class WindowIcon
{
    private static string? _path;

    public static string? Path
    {
        get
        {
            if (_path is not null) return _path;
            try { return _path = Assets.Extract("AppIcon.ico").Path; } catch { return null; }
        }
    }

    public static void Apply(Window window)
    {
        try { if (Path is { } p) window.AppWindow.SetIcon(p); } catch (Exception e) { Log.Write("設定視窗圖示失敗 " + e.Message); }
    }
}
