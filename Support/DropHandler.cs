using PhoneCentre.Adb;

namespace PhoneCentre.Support;

/// <summary>拖進來的檔案：apk 安裝；照片影片存到 DCIM/Camera；其他存到 Download</summary>
public static class DropHandler
{
    private static readonly HashSet<string> MediaExts = ["png", "jpeg", "jpg", "heic", "heif", "mov", "mp4"];

    public static string? Destination(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (ext == "apk") return null;
        var name = Path.GetFileName(path);
        return (MediaExts.Contains(ext) ? "/sdcard/DCIM/Camera/" : "/sdcard/Download/") + name;
    }

    /// <summary>依序處理，透過 progress 回報狀態文字</summary>
    public static async Task HandleAsync(IReadOnlyList<string> paths, Device device, Action<string> progress)
    {
        var c = device.Client;
        var failures = new List<string>();
        for (int i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            var prefix = paths.Count > 1 ? $"({i + 1}/{paths.Count}) " : "";
            try
            {
                if (Directory.Exists(path)) { progress(prefix + $"正在傳送資料夾 {name}…"); await c.PushAsync(path, "/sdcard/Download/"); }
                else if (Destination(path) is { } dest) { progress(prefix + $"正在傳送 {name}…"); await c.PushAsync(path, dest); }
                else { progress(prefix + $"正在安裝 {name}…"); await c.InstallAsync(path); }
            }
            catch (Exception e) { failures.Add($"{name}：{e.Message}"); }
        }
        progress(failures.Count == 0 ? "完成" : "失敗：\n" + string.Join("\n", failures));
    }
}
