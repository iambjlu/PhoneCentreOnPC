using Microsoft.UI.Dispatching;
using PhoneCentre.Adb;
using PhoneCentre.Settings;
using PhoneCentre.Support;

namespace PhoneCentre.Services;

public enum AdbServerStatus { Unknown, MissingAdb, Running, Down }

/// <summary>
/// 管理裝置清單、自動維持 adb server 與無線裝置重連（對應 DeviceManager.swift；遠端 PhoneCentre 暫時不做）。
/// 所有成員只在 UI 執行緒存取。
/// </summary>
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
    private readonly Dictionary<string, (DateTime Next, double Delay)> _backoff = new();

    public IReadOnlyList<Device> Devices => _devices;
    public AdbServerStatus ServerStatus { get; private set; } = AdbServerStatus.Unknown;
    public string LastEvent { get; private set; } = "";
    /// <summary>正在重連的無線位址</summary>
    public HashSet<string> Reconnecting { get; } = [];
    /// <summary>使用者手動中斷連線、但保留在清單上的無線位址：不自動重連，直到使用者自己按「連線」（只在這次執行期間有效）</summary>
    public HashSet<string> Paused { get; } = [];
    public event Action? Changed;

    public IEnumerable<Device> OnlineDevices => _devices.Where(d => d.IsOnline);

    public void Start()
    {
        if (_timer is not null) return;
        _queue = DispatcherQueue.GetForCurrentThread();
        _ = RefreshAsync();
        _timer = _queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => _ = RefreshAsync();
        _timer.Start();
    }

    public Device? Device(string id) => _devices.FirstOrDefault(d => d.Id == id);

    public AppCatalog Catalog(Device device)
    {
        if (_catalogs.TryGetValue(device.Id, out var c)) { c.Device = device; return c; }
        return _catalogs[device.Id] = new AppCatalog(device);
    }

    // MARK: 輪詢

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
            all = Adb.Device.Parse(text, AdbEndpoint.Local);
        }
        catch (Exception)
        {
            ServerStatus = AdbServerStatus.Down;
            if (AppSettings.KeepAlive && (DateTime.Now - _lastServerStart).TotalSeconds > 4)
            {
                _lastServerStart = DateTime.Now;
                Log("adb server 不在執行，正在重新啟動…");
                await AdbClient.StartServerAsync();
                // server 重啟後，所有無線連線都會消失，立刻重連
                _backoff.Clear();
            }
        }

        foreach (var d in all) d.Info = _infoCache.GetValueOrDefault(d.Id);
        bool changed = before != ServerStatus || !SameList(all, _devices);
        if (changed) { _devices = all; CacheManager.Shared.DevicesChanged(all); }

        foreach (var d in all.Where(d => d.IsOnline && !_infoCache.ContainsKey(d.Id) && !_infoLoading.Contains(d.Id)))
            LoadInfo(d);
        if (ServerStatus == AdbServerStatus.Running) MaintainWireless(all);
        if (changed) Changed?.Invoke();
    }

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
            if (d.IsWireless && !d.IsRemote && AppSettings.Data.RememberWireless) Remember(d.Serial, info);
            Changed?.Invoke();
        }));
    }

    // MARK: 無線裝置

    /// <summary>記住連線過的無線裝置（同一支手機換 port 會更新舊紀錄）</summary>
    private void Remember(string address, DeviceInfo? info)
    {
        if (!address.Contains(':') || address.Contains("._adb-tls")) return;
        AppSettings.Update(d =>
        {
            var list = d.SavedEndpoints;
            string? Name() => info is null ? null : (info.DeviceName.Length == 0 ? info.Model : info.DeviceName);
            var existing = list.FirstOrDefault(x => x.Address == address);
            if (existing is not null) { if (info is not null) { existing.SerialNo = info.SerialNo; existing.Name = Name(); } return; }
            bool? auto = null;
            if (info is not null && list.FirstOrDefault(x => x.SerialNo == info.SerialNo) is { } same) { auto = same.AutoReconnect; list.Remove(same); }
            list.Add(new SavedEndpoint { Address = address, SerialNo = info?.SerialNo, Name = Name(), AutoReconnect = auto });
        });
    }

    /// <summary>從記住的無線裝置清單移除，並中斷連線</summary>
    public void Forget(SavedEndpoint ep)
    {
        AppSettings.Update(d => d.SavedEndpoints.RemoveAll(x => x.Address == ep.Address));
        Paused.Remove(ep.Address);
        _ = AdbClient.DisconnectAsync(ep.Address);
        Changed?.Invoke();
    }

    /// <summary>清單上記住的無線裝置（依位址或 ro.serialno 找）</summary>
    public SavedEndpoint? Saved(Device d) =>
        AppSettings.Data.SavedEndpoints.FirstOrDefault(x => x.Address == d.Serial)
        ?? (d.Info is { } i ? AppSettings.Data.SavedEndpoints.FirstOrDefault(x => x.SerialNo == i.SerialNo) : null);

    /// <summary>設定這台無線裝置斷線時要不要自動重新連線</summary>
    public void SetAutoReconnect(string address, bool on)
    {
        AppSettings.Update(d => { if (d.SavedEndpoints.FirstOrDefault(x => x.Address == address) is { } ep) ep.AutoReconnect = on; });
        Changed?.Invoke();
    }

    /// <summary>中斷連線但保留在清單上：這次執行期間不會自動重連，之後可以從清單一鍵連回去</summary>
    public async Task DisconnectKeepingAsync(Device d)
    {
        if (d.IsWireless && !d.IsRemote && AppSettings.Data.RememberWireless && Saved(d) is null) Remember(d.Serial, d.Info);
        Paused.Add(d.Serial);
        _backoff.Remove(d.Serial);
        await AdbClient.DisconnectAsync(d.Serial, d.Endpoint);
        await RefreshAsync();
    }

    /// <summary>從清單手動連回去（清掉「已中斷」狀態）</summary>
    public async Task ReconnectSavedAsync(SavedEndpoint ep)
    {
        Paused.Remove(ep.Address);
        _backoff.Remove(ep.Address);
        await ReconnectAsync(ep);
        await RefreshAsync();
    }

    /// <summary>斷線的無線裝置：以指數退避重連（最長 60 秒）</summary>
    private void MaintainWireless(List<Device> online)
    {
        if (!AppSettings.KeepAlive) return;
        var localOnline = online.Where(d => !d.IsRemote && d.IsOnline).Select(d => d.Serial).ToHashSet();
        var onlineSerialNos = online.Where(d => d.IsOnline && d.Info is not null).Select(d => d.Info!.SerialNo).ToHashSet();
        foreach (var ep in AppSettings.Data.SavedEndpoints.ToList())
        {
            if (localOnline.Contains(ep.Address)) { _backoff.Remove(ep.Address); continue; }
            // 使用者關掉自動重連，或手動中斷連線（保留在清單）的，不自動連
            if (!ep.Auto || Paused.Contains(ep.Address)) continue;
            // 同一台手機已經用別的方式（USB、新 port）連上了，就不重連
            if (ep.SerialNo is { Length: > 0 } sn && onlineSerialNos.Contains(sn)) continue;
            if (_backoff.TryGetValue(ep.Address, out var b) && b.Next > DateTime.Now) continue;
            _ = ReconnectAsync(ep);
            double delay = Math.Min((_backoff.TryGetValue(ep.Address, out var o) ? o.Delay : 2.5) * 2, 60);
            _backoff[ep.Address] = (DateTime.Now.AddSeconds(delay), delay);
        }
    }

    private async Task ReconnectAsync(SavedEndpoint ep)
    {
        if (!Reconnecting.Add(ep.Address)) return;
        Changed?.Invoke();
        try
        {
            // 若裝置還在清單中但離線，先斷開再連
            if (_devices.Any(d => d.Serial == ep.Address && !d.IsOnline)) await AdbClient.DisconnectAsync(ep.Address);
            var r = await AdbClient.ConnectAsync(ep.Address);
            if (r.Ok) { Log($"已重新連線 {ep.Name ?? ep.Address}"); _backoff.Remove(ep.Address); return; }
            // 無線偵錯的 port 可能變了：用 mDNS 找同一台手機
            if (ep.SerialNo is { Length: > 0 } sn)
            {
                foreach (var svc in (await AdbClient.MdnsServicesAsync()).Where(s => s.Type.Contains("connect") && s.Instance.Contains(sn)))
                {
                    var r2 = await AdbClient.ConnectAsync(svc.Address);
                    if (!r2.Ok) continue;
                    Log($"{ep.Name ?? sn} 的 port 已變更為 {svc.Address}，已重新連線");
                    AppSettings.Update(d =>
                    {
                        d.SavedEndpoints.RemoveAll(x => x.Address == ep.Address);
                        d.SavedEndpoints.Add(new SavedEndpoint { Address = svc.Address, SerialNo = sn, Name = ep.Name, AutoReconnect = ep.AutoReconnect });
                    });
                    return;
                }
            }
        }
        finally { Reconnecting.Remove(ep.Address); Changed?.Invoke(); }
    }

    // MARK: 使用者動作

    public async Task<(bool Ok, string Message)> ConnectAsync(string address)
    {
        var addr = address.Trim();
        if (!addr.Contains(':')) addr += ":5555";
        Paused.Remove(addr);
        var r = await AdbClient.ConnectAsync(addr);
        if (r.Ok && AppSettings.Data.RememberWireless) Remember(addr, null);
        await RefreshAsync();
        return r;
    }

    /// <summary>USB 裝置轉無線：adb tcpip 5555，取得手機 Wi-Fi IP 後 adb connect</summary>
    public async Task<(bool Ok, string Message)> EnableWirelessAsync(Device device)
    {
        var c = device.Client;
        string o = ""; try { o = await c.ShellAsync("ip -f inet addr show wlan0 | grep -o 'inet [0-9.]*' | head -1"); } catch { }
        var ip = o.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
        if (string.IsNullOrEmpty(ip)) return (false, "取不到手機的 Wi-Fi IP，請確認手機已連上 Wi-Fi");
        try
        {
            var r = await c.CliAsync(["tcpip", "5555"]);
            if (r.Status != 0) return (false, "adb tcpip 5555 失敗");
        }
        catch { return (false, "adb tcpip 5555 失敗"); }
        await Task.Delay(1800);
        var result = await ConnectAsync($"{ip}:5555");
        return result.Ok ? (true, $"已切換為無線：{ip}:5555，現在可以拔掉 USB") : result;
    }

    /// <summary>中斷連線並從清單移除</summary>
    public async Task DisconnectAsync(Device d)
    {
        AppSettings.Update(x => x.SavedEndpoints.RemoveAll(e => e.Address == d.Serial));
        Paused.Remove(d.Serial);
        await AdbClient.DisconnectAsync(d.Serial, d.Endpoint);
        await RefreshAsync();
    }

    public void Log(string s)
    {
        LastEvent = s;
        Support.Log.Write("[DeviceManager] " + s);
        Changed?.Invoke();
    }
}
