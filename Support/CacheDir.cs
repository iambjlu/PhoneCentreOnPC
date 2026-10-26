namespace PhoneCentre.Support;

/// <summary>快取放在 %LOCALAPPDATA%\PhoneCentre\Cache\&lt;裝置鍵&gt;\{icons,thumbs,files}</summary>
public static class CacheDir
{
    public static string For(Adb.Device device, string kind)
    {
        var dir = Path.Combine(Assets.SupportDir, "Cache", device.Key, kind);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
