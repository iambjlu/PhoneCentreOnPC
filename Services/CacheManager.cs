using Microsoft.UI.Dispatching;
using System.Text.Json;
using PhoneCentre.Adb;
using PhoneCentre.Settings;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

/// <summary>
/// 管理所有暫存（App 圖示、相片縮圖、預覽下載檔），依使用者設定的容量上限與清除時機清理（對應 CacheManager.swift）。
/// 結構：%LOCALAPPDATA%\PhoneCentre\Cache\&lt;裝置鍵&gt;\{icons,thumbs,files}
/// </summary>
public sealed class CacheManager
{
    public static CacheManager Shared { get; } = new();

    public long TotalBytes { get; private set; }
    public event Action? Changed;

    private readonly DispatcherQueue? _ui = DispatcherQueue.GetForCurrentThread();
    private DispatcherQueueTimer? _timer;
    private Dictionary<string, DateTimeOffset> _lastSeen = new();
    private HashSet<string> _onlineKeys = [];
    private static string Root { get { var d = Path.Combine(Assets.SupportDir, "Cache"); Directory.CreateDirectory(d); return d; } }
    private static string LastSeenFile => Path.Combine(Root, "last-seen.json");

    private CacheManager()
    {
        try { _lastSeen = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(LastSeenFile)) ?? new(); } catch { }
    }

    public void Start()
    {
        if (_timer is not null || _ui is null) return;
        Enforce();
        _timer = _ui.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(60);
        _timer.Tick += (_, _) => Enforce();
        _timer.Start();
    }

    /// <summary>DeviceManager 每次更新裝置清單時呼叫</summary>
    public void DevicesChanged(IEnumerable<Device> devices)
    {
        var now = devices.Where(d => d.IsOnline).Select(d => d.Key).ToHashSet();
        foreach (var k in now) _lastSeen[k] = DateTimeOffset.Now;
        var gone = _onlineKeys.Except(now).ToList();
        _onlineKeys = now;
        if (gone.Count > 0 && AppSettings.Data.CacheClearPolicy == CacheClearPolicy.OnDisconnect)
        {
            foreach (var k in gone) try { Directory.Delete(Path.Combine(Root, k), true); } catch { }
            Recalc();
        }
        SaveLastSeen();
    }

    private void SaveLastSeen()
    {
        try { File.WriteAllText(LastSeenFile, JsonSerializer.Serialize(_lastSeen)); } catch { }
    }

    /// <summary>套用清除時機與容量上限</summary>
    public void Enforce()
    {
        var s = AppSettings.Data;
        if (s.CacheClearPolicy == CacheClearPolicy.AfterDays)
        {
            var limit = DateTimeOffset.Now.AddDays(-Math.Max(s.CacheClearDays, 1));
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var k = Path.GetFileName(dir);
                if (_onlineKeys.Contains(k)) continue;
                if (_lastSeen.GetValueOrDefault(k, DateTimeOffset.MinValue) < limit)
                {
                    try { Directory.Delete(dir, true); } catch { }
                    _lastSeen.Remove(k);
                }
            }
            SaveLastSeen();
        }
        Trim(Math.Max(s.CacheLimitMB, 10) * 1_048_576L);
    }

    /// <summary>依最後使用時間（LRU）刪除，直到低於上限</summary>
    private void Trim(long limit)
    {
        _ = Task.Run(() =>
        {
            var files = new List<(string Path, long Size, DateTime Used)>();
            long total = 0;
            try
            {
                foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories))
                {
                    if (f.Name == "last-seen.json") continue;
                    total += f.Length;
                    // NTFS 常常不更新最後存取時間，所以取存取與寫入時間較晚的
                    files.Add((f.FullName, f.Length, f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc));
                }
                if (total > limit)
                {
                    foreach (var f in files.OrderBy(x => x.Used))
                    {
                        try { File.Delete(f.Path); total -= f.Size; } catch { }
                        if (total <= limit * 9 / 10) break;     // 多清一點，避免頻繁觸發
                    }
                }
            }
            catch { }
            _ui?.TryEnqueue(() => { TotalBytes = total; Changed?.Invoke(); });
        });
    }

    public void Recalc() => Trim(Math.Max(AppSettings.Data.CacheLimitMB, 10) * 1_048_576L);

    public void ClearAll()
    {
        foreach (var d in Directory.GetDirectories(Root)) try { Directory.Delete(d, true); } catch { }
        _lastSeen.Clear(); TotalBytes = 0; SaveLastSeen();
        Changed?.Invoke();
    }
}
