using System.Security.Cryptography;
using System.Text;
using PhoneCentre.Adb;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

/// <summary>手機上的一個檔案或資料夾</summary>
public sealed record RemoteFile(string Path, string Name, bool IsDir, long Size, DateTimeOffset Modified)
{
    public string Ext => IsDir ? "" : System.IO.Path.GetExtension(Name).TrimStart('.').ToLowerInvariant();
    public bool IsImage => Ext is "png" or "jpg" or "jpeg" or "heic" or "heif" or "gif" or "webp" or "bmp" or "dng";
    public bool IsVideo => Ext is "mp4" or "mov" or "mkv" or "3gp" or "webm" or "avi";
    public bool IsMedia => IsImage || IsVideo;

    /// <summary>種類（Mac 版用系統的 UTType 描述；這裡對常見副檔名給中文名稱，其餘顯示大寫副檔名）</summary>
    public string Kind => IsDir ? "資料夾" : Ext switch
    {
        "jpg" or "jpeg" => "JPEG 圖片", "png" => "PNG 圖片", "heic" or "heif" => "HEIC 圖片", "gif" => "GIF 圖片", "webp" => "WebP 圖片",
        "mp4" => "MPEG-4 影片", "mov" => "QuickTime 影片", "mkv" => "Matroska 影片", "3gp" => "3GP 影片",
        "mp3" => "MP3 音訊", "m4a" => "MPEG-4 音訊", "wav" => "WAV 音訊", "flac" => "FLAC 音訊", "ogg" or "opus" => "Ogg 音訊",
        "pdf" => "PDF 文件", "txt" or "log" => "純文字", "xml" => "XML 文字", "json" => "JSON", "html" => "HTML 文件",
        "apk" => "Android 套件", "zip" => "ZIP 封存檔", "7z" => "7z 封存檔", "rar" => "RAR 封存檔",
        "doc" or "docx" => "Word 文件", "xls" or "xlsx" => "Excel 試算表", "ppt" or "pptx" => "PowerPoint 簡報",
        "" => "檔案", _ => Ext.ToUpperInvariant(),
    };

    /// <summary>圖示名稱（Material）</summary>
    public string IconName => IsDir ? "folder" : IsImage ? "image" : IsVideo ? "movie"
        : Ext is "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" ? "music_note"
        : Ext == "apk" ? "android" : Ext is "zip" or "7z" or "rar" ? "folder_zip" : Ext == "pdf" ? "picture_as_pdf" : "description";

    public string SizeString => IsDir ? "—" : FileSize.Format(Size);
}

public static class FileSize
{
    /// <summary>像 macOS 的 ByteCountFormatter（十進位）：20 KB、1.5 MB</summary>
    public static string Format(long bytes)
    {
        if (bytes < 1000) return $"{bytes} 位元組";
        double v = bytes; string[] u = ["KB", "MB", "GB", "TB"]; int i = -1;
        do { v /= 1000; i++; } while (v >= 1000 && i < u.Length - 1);
        return (v >= 100 || Math.Abs(v - Math.Round(v)) < 0.05 ? $"{v:0}" : $"{v:0.#}") + " " + u[i];
    }
}

/// <summary>手機檔案操作（透過 adb shell / push / pull），對應 RemoteFiles.swift 的 RemoteFS</summary>
public readonly record struct RemoteFS(Device Device)
{
    private AdbClient Client => Device.Client;

    public async Task<List<RemoteFile>> ListAsync(string dir, bool showHidden)
    {
        var globs = showHidden ? "* .[!.]* ..?*" : "*";
        // stat -L：跟隨連結（/sdcard 等是 symlink）；%F 類型、%s 大小、%Y 修改時間、%n 名稱
        var outp = await Client.ShellAsync($"cd {Shell.Quote(dir)} 2>&1 || exit 1; stat -L -c '%F|%s|%Y|%n' -- {globs} 2>/dev/null", 30);
        if (outp.Contains("can't cd") || outp.Contains("No such file") || outp.Contains("Permission denied"))
            throw new AdbException(outp.Trim());
        var b = dir.EndsWith('/') ? dir : dir + "/";
        var list = new List<RemoteFile>();
        foreach (var line in outp.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('|', 4);
            if (p.Length != 4 || p[3] is "*" or ".[!.]*" or "..?*") continue;
            long.TryParse(p[1], out var size); long.TryParse(p[2], out var ts);
            list.Add(new RemoteFile(b + p[3], p[3], p[0].Contains("directory"), size, DateTimeOffset.FromUnixTimeSeconds(ts)));
        }
        return list;
    }

    /// <summary>資料夾底下所有檔案（含子資料夾，略過隱藏檔與隱藏資料夾，例如 .thumbnails）</summary>
    public async Task<List<RemoteFile>> ListRecursiveAsync(string dir)
    {
        var outp = await Client.ShellAsync(
            $"cd {Shell.Quote(dir)} 2>&1 || exit 1; find . -type f ! -path '*/.*' -exec stat -L -c '%F|%s|%Y|%n' {{}} + 2>/dev/null", 120);
        if (outp.Contains("can't cd") || outp.Contains("No such file")) throw new AdbException(outp.Trim());
        var b = dir.EndsWith('/') ? dir[..^1] : dir;
        var list = new List<RemoteFile>();
        foreach (var line in outp.Split('\n'))
        {
            var p = line.TrimEnd('\r').Split('|', 4);
            if (p.Length != 4 || !p[3].StartsWith("./")) continue;
            var rel = p[3][2..];
            long.TryParse(p[1], out var size); long.TryParse(p[2], out var ts);
            list.Add(new RemoteFile(b + "/" + rel, rel[(rel.LastIndexOf('/') + 1)..], false, size, DateTimeOffset.FromUnixTimeSeconds(ts)));
        }
        return list;
    }

    public async Task MkdirAsync(string path) => Check(await Client.ShellAsync($"mkdir -p {Shell.Quote(path)} 2>&1"));

    public async Task RenameAsync(string path, string newPath) =>
        Check(await Client.ShellAsync($"mv -n {Shell.Quote(path)} {Shell.Quote(newPath)} 2>&1"));

    public async Task DeleteAsync(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        Check(await Client.ShellAsync("rm -rf " + string.Join(" ", list.Select(Shell.Quote)) + " 2>&1"));
        foreach (var p in list)   // 通知媒體庫
        {
            try { await Client.ShellAsync($"am broadcast -a android.intent.action.MEDIA_SCANNER_SCAN_FILE -d {Shell.Quote("file://" + p)} >/dev/null 2>&1"); } catch { }
        }
    }

    /// <summary>下載到暫存（預覽、開啟用），已下載過且大小相同就不重抓</summary>
    public async Task<string> DownloadToCacheAsync(RemoteFile f)
    {
        var local = System.IO.Path.Combine(CacheDir.For(Device, "files"), StableHash(f.Path), f.Name);
        if (File.Exists(local) && new FileInfo(local).Length == f.Size) return local;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(local)!);
        await Client.PullAsync(f.Path, local);
        return local;
    }

    private static void Check(string outp)
    {
        var t = outp.Trim();
        if (t.Length > 0) throw new AdbException(t);
    }

    /// <summary>跨程序穩定的短雜湊</summary>
    public static string StableHash(string s) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)).AsSpan(0, 8)).ToLowerInvariant();
}
