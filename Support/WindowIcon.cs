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

    /// <summary>
    /// 把 App 的 PNG 圖示包成 .ico（ICO 容器可以直接放 PNG）設為視窗圖示；獨立視窗的標題列與工作列顯示目前的 App。
    /// 沒有圖示或失敗時維持原本的 PhoneCentre 圖示
    /// </summary>
    public static void ApplyApp(Window window, string? pngPath)
    {
        if (pngPath is null) return;
        try
        {
            var png = File.ReadAllBytes(pngPath);
            if (png.Length < 24) return;
            int w = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            int h = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            using var ms = new MemoryStream();
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((short)0); bw.Write((short)1); bw.Write((short)1);                  // ICONDIR：類型 1、一張圖
                bw.Write((byte)(w >= 256 ? 0 : w)); bw.Write((byte)(h >= 256 ? 0 : h));      // 0＝256
                bw.Write((byte)0); bw.Write((byte)0); bw.Write((short)1); bw.Write((short)32);
                bw.Write(png.Length); bw.Write(22);                                          // 資料大小與位移（標頭 6＋項目 16）
                bw.Write(png);
            }
            var ico = System.IO.Path.ChangeExtension(pngPath, ".ico");
            File.WriteAllBytes(ico, ms.ToArray());
            window.AppWindow.SetIcon(ico);
        }
        catch (Exception e) { Log.Write("設定 App 視窗圖示失敗 " + e.Message); }
    }

    public static void Apply(Window window)
    {
        try { if (Path is { } p) window.AppWindow.SetIcon(p); } catch (Exception e) { Log.Write("設定視窗圖示失敗 " + e.Message); }
    }
}
