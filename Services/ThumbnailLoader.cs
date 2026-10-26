using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Adb;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

/// <summary>
/// 動態載入縮圖（對應 ThumbnailLoader）：看得到的格子才排隊，80ms 內的請求合併成一批，在手機端（輔助程式 thumbs）產生縮圖，
/// 只傳小圖不傳原檔，並存到磁碟快取。只在 UI 執行緒呼叫。
/// </summary>
public sealed class ThumbnailLoader
{
    private readonly Device _device;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly Dictionary<string, BitmapImage> _images = new();
    private readonly List<RemoteFile> _queue = [];
    private readonly HashSet<string> _inFlight = [], _wanted = [];
    private bool _running;

    /// <summary>某張縮圖載好了（參數是遠端路徑）</summary>
    public event Action<string>? Loaded;

    public ThumbnailLoader(Device device) => _device = device;

    private string CacheFile(RemoteFile f) =>
        Path.Combine(CacheDir.For(_device, "thumbs"), RemoteFS.StableHash($"v2|{f.Path}|{f.Size}|{f.Modified.ToUnixTimeSeconds()}") + ".jpg");

    private static BitmapImage Load(string file) => new() { UriSource = new Uri(file), DecodePixelWidth = 320 };

    public BitmapImage? Get(RemoteFile f) => _images.GetValueOrDefault(f.Path);

    /// <summary>格子出現：有快取就直接用，沒有就排隊</summary>
    public BitmapImage? Appear(RemoteFile f)
    {
        _wanted.Add(f.Path);
        if (_images.TryGetValue(f.Path, out var img)) return img;
        if (_inFlight.Contains(f.Path)) return null;
        var cache = CacheFile(f);
        if (File.Exists(cache)) return _images[f.Path] = Load(cache);
        _queue.Add(f); _inFlight.Add(f.Path);
        Pump();
        return null;
    }

    /// <summary>捲出畫面的格子不再需要</summary>
    public void Disappear(RemoteFile f)
    {
        _wanted.Remove(f.Path);
        _queue.RemoveAll(q => q.Path == f.Path);
        if (!_images.ContainsKey(f.Path)) _inFlight.Remove(f.Path);
    }

    private void Pump()
    {
        if (_running) return;
        _running = true;
        _ = Task.Run(async () =>
        {
            await Task.Delay(80);   // 稍等讓同一次捲動的格子一起批次處理
            while (true)
            {
                List<RemoteFile> batch = [];
                var tcs = new TaskCompletionSource();
                // 判斷佇列是否空了與清除 _running 要在 UI 執行緒一起做，否則剛好在這之間出現的新格子會被漏掉
                _ui.TryEnqueue(() =>
                {
                    batch = _queue.Take(24).ToList(); _queue.RemoveRange(0, batch.Count);
                    if (batch.Count == 0) _running = false;
                    tcs.SetResult();
                });
                await tcs.Task;
                if (batch.Count == 0) break;

                string outp = "";
                try { outp = await HelperTool.Shared.RunAsync(_device, ["thumbs", "256", .. batch.Select(b => b.Path)], 90); } catch { }
                var done = new List<(RemoteFile F, string File)>();
                foreach (var line in outp.Split('\n'))
                {
                    var p = line.Trim().Split('\t', 2);
                    if (p.Length != 2) continue;
                    var f = batch.FirstOrDefault(b => b.Path == p[0]);
                    if (f is null) continue;
                    try { var file = CacheFile(f); File.WriteAllBytes(file, Convert.FromBase64String(p[1])); done.Add((f, file)); } catch { }
                }
                _ui.TryEnqueue(() =>
                {
                    foreach (var (f, file) in done) { _images[f.Path] = Load(file); Loaded?.Invoke(f.Path); }
                    foreach (var f in batch.Where(b => !_images.ContainsKey(b.Path))) _inFlight.Remove(f.Path);
                });
            }
        });
    }
}
