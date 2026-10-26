using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PhoneCentre.Support;

/// <summary>
/// 內嵌字型：英數字用 Inter、中文用台北黑體 Beta（兩者都是 SIL OFL 1.1 開源字型）。
/// WinUI 的文字用 DirectWrite 的系統字型集合，只認系統字型或 ms-appx 檔案；單一 exe 旁邊沒有字型檔，
/// 而用 AddFontResourceEx(FR_PRIVATE) 登記的程序專用字型 DirectWrite 看不到（實測過）。
/// 所以啟動時把字型暫時安裝成「目前使用者」的字型（不需要管理員權限：複製到 %LOCALAPPDATA%\Microsoft\Windows\Fonts、
/// 登記在 HKCU 的 Fonts 機碼），程式結束時再移除。必須在 XAML 第一次用到字型之前完成。
/// </summary>
public static class Fonts
{
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern int AddFontResource(string name);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] private static extern bool RemoveFontResource(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    private const string RegKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
    private static readonly IntPtr HwndBroadcast = new(0xffff);
    private const uint WM_FONTCHANGE = 0x1D;

    /// <summary>字型家族名稱：英數字先用 Inter，沒有的字（中文）用台北黑體</summary>
    public const string Family = "Inter, Taipei Sans TC Beta";

    private static readonly List<(string ValueName, string Path)> Installed = [];

    /// <summary>資源名稱 → 登記名稱（"font-Inter-Bold.ttf" → "Inter Bold (TrueType)"）</summary>
    private static string ValueNameFor(string file)
    {
        var n = System.IO.Path.GetFileNameWithoutExtension(file);                 // Inter-Bold、TaipeiSansTC-Regular
        n = n.Replace("TaipeiSansTC", "Taipei Sans TC Beta").Replace('-', ' ');
        return n + " (TrueType)";
    }

    public static void Install()
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(dir);
            using var key = Registry.CurrentUser.CreateSubKey(RegKey);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("font-") && n.EndsWith(".ttf")))
            {
                var file = "PhoneCentre-" + res["font-".Length..];
                var path = System.IO.Path.Combine(dir, file);
                using (var src = asm.GetManifestResourceStream(res)!)
                {
                    if (!File.Exists(path) || new FileInfo(path).Length != src.Length)
                    {
                        using var dst = File.Create(path);
                        src.CopyTo(dst);
                    }
                }
                var name = ValueNameFor(res["font-".Length..]);
                key.SetValue(name, path);
                AddFontResource(path);
                Installed.Add((name, path));
            }
            SendMessageTimeout(HwndBroadcast, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, 2, 1000, out _);
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Uninstall();
            Log.Write($"已暫時安裝 {Installed.Count} 個字型");
        }
        catch (Exception e) { Log.Write("安裝字型失敗 " + e.Message); }
    }

    /// <summary>程式結束時移除（字型還被別的程序用著就刪不掉檔案，下次啟動會覆蓋）</summary>
    public static void Uninstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegKey, true);
            foreach (var (name, path) in Installed)
            {
                key?.DeleteValue(name, false);
                RemoveFontResource(path);
                try { File.Delete(path); } catch { }
            }
            SendMessageTimeout(HwndBroadcast, WM_FONTCHANGE, IntPtr.Zero, IntPtr.Zero, 2, 500, out _);
        }
        catch { }
    }

    /// <summary>把全域預設字型換成 Inter＋台北黑體（必須在建立任何視窗之前呼叫）</summary>
    public static void Apply(Application app)
    {
        var fam = new FontFamily(Family);
        app.Resources["ContentControlThemeFontFamily"] = fam;
        app.Resources["TextBlockFontFamily"] = fam;
        var style = new Style(typeof(Microsoft.UI.Xaml.Controls.TextBlock));
        style.Setters.Add(new Setter(Microsoft.UI.Xaml.Controls.TextBlock.FontFamilyProperty, fam));
        app.Resources[typeof(Microsoft.UI.Xaml.Controls.TextBlock)] = style;
    }
}
