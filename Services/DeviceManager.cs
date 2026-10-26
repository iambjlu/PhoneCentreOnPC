using Microsoft.UI.Dispatching;
using PhoneCentre.Adb;
using PhoneCentre.Settings;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

public enum AdbServerStatus { Unknown, MissingAdb, Running, Down }

/// <summary>管理裝置清單、自動維持 adb server（對應 DeviceManager.swift；無線重連與遠端之後再補）。所有成員只在 UI 執行緒存取。</summary>
public sealed class DeviceManager
{
    public static DeviceManager Shared { get; } = new();

    private List<Device> _devices = [];
    private readonly Dictionary<string, DeviceInfo> _infoCache = new();
    private readonly HashSet<string> _infoLoading = [];
    private readonly Dictionary<string, AppCatalog> _catalogs = new();
    private DispatcherQueueTimer? _timer;
    private DispatcherQueue? _queue;
    private bool _refreshing;
    private DateTime _lastServerStart = DateTime.MinValue;

    public IReadOnlyList<Device> Devices => _devices;
    public AdbServerStatus ServerStatus { get; private set; } = AdbServerStatus.Unknown;
    public string LastEvent { get; private set; } = "";
    public event Action? Changed;

    public IEnumerable<Device> OnlineDevices => _devices.Where(d => d.IsOnline);

    public void Start()
    {
        if (_timer is not null) return;
        _ = RefreshAsync();
        _queue = DispatcherQueue.GetForCurrentThread();
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    /// <summary>從記住的無線裝置清單移除，並中斷連線</summary>
    public void Forget(Settings.SavedEndpoint ep)
    {
        Settings.AppSettings.Update(d => d.SavedEndpoints.RemoveAll(x => x.Address == ep.Address));
        _ = AdbClient.DisconnectAsync(ep.Address);
    }

    public Device? Device(string id) => _devices.FirstOrDefault(d => d.Id == id);

    public AppCatalog Catalog(Device device)
    {
        if (_catalogs.TryGetValue(device.Id, out var c)) { c.Device = device; return c; }
        return _catalogs[device.Id] = new AppCatalog(device);
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try { await RefreshCoreAsync(); }
        finally { _refreshing = false; }
    }

    private async Task RefreshCoreAsync()
    {
        var before = ServerStatus;
        if (await Task.Run(() => AdbLocator.Find()) is null)
        {
            ServerStatus = AdbServerStatus.MissingAdb;
            if (_devices.Count > 0) _devices = [];
            if (before != ServerStatus) Changed?.Invoke();
            return;
        }

        List<Device> all = [];
        try
        {
            var text = await Task.Run(() => AdbSocket.HostQuery(AdbEndpoint.Local, "host:devices-l"));
            if (ServerStatus == AdbServerStatus.Down) Log("adb server 已恢復");
            ServerStatus = AdbServerStatus.Running;
            all = Device_Parse(text);
        }
        catch (Exception)
        {
            ServerStatus = AdbServerStatus.Down;
            if (AppSettings.KeepAlive && (DateTime.Now - _lastServerStart).TotalSeconds > 4)
            {
                _lastServerStart = DateTime.Now;
                Log("adb server 不在執行，正在重新啟動…");
                await AdbClient.StartServerAsync();
            }
        }

        foreach (var d in all) d.Info = _infoCache.GetValueOrDefault(d.Id);
        bool changed = before != ServerStatus || !SameList(all, _devices);
        if (changed) { _devices = all; CacheManager.Shared.DevicesChanged(all); }

        foreach (var d in all.Where(d => d.IsOnline && !_infoCache.ContainsKey(d.Id) && !_infoLoading.Contains(d.Id)))
            LoadInfo(d);
        if (changed) Changed?.Invoke();
    }

    private static List<Device> Device_Parse(string text) => Adb.Device.Parse(text, AdbEndpoint.Local);

    private static bool SameList(List<Device> a, List<Device> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First.Serial == p.Second.Serial && p.First.State == p.Second.State
            && p.First.Model == p.Second.Model && (p.First.Info is null) == (p.Second.Info is null));

    private void LoadInfo(Device d)
    {
        _infoLoading.Add(d.Id);
        _ = Task.Run(() => DeviceInfo.FetchAsync(d.Client)).ContinueWith(t => _queue?.TryEnqueue(() =>
        {
            _infoLoading.Remove(d.Id);
            var info = t.Result;
            if (info is null) return;
            _infoCache[d.Id] = info;
            var cur = _devices.FirstOrDefault(x => x.Id == d.Id);
            if (cur is not null) cur.Info = info;
            Changed?.Invoke();
        }));
    }

    public void Log(string s)
    {
        LastEvent = s;
        Support.Log.Write("[DeviceManager] " + s);
        Changed?.Invoke();
    }
}
