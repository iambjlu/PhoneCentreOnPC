using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using PhoneCentre.Adb;
using PhoneCentre.Support;
using PhoneCentre.Ui;

namespace PhoneCentre.Mirror;

/// <summary>用地圖選一個位置，透過 adb 的測試定位提供者（cmd location providers）讓手機以為人在那裡（對應 LocationSimulator.swift）</summary>
public static class LocationSimulator
{
    private static string Loc(double lat, double lng) => $"{lat.ToString("0.000000", CultureInfo.InvariantCulture)},{lng.ToString("0.000000", CultureInfo.InvariantCulture)}";

    public static async Task<bool> ApplyAsync(Device d, double lat, double lng)
    {
        var loc = Loc(lat, lng);
        var script = "appops set com.android.shell android:mock_location allow; appops set android android:mock_location allow; ";
        foreach (var p in new[] { "gps", "network" })
        {
            script += $"cmd location providers add-test-provider {p} 2>/dev/null; ";
            script += $"cmd location providers set-test-provider-enabled {p} true; ";
            script += $"cmd location providers set-test-provider-location {p} --location {loc} --accuracy 5; ";
        }
        try { var o = await d.Client.ShellAsync(script, 15); return !o.Contains("Exception") && !o.Contains("Error"); } catch { return false; }
    }

    /// <summary>只重送位置（模擬中定時呼叫，避免手機改回真實定位）</summary>
    public static async Task RefreshAsync(Device d, double lat, double lng)
    {
        var loc = Loc(lat, lng);
        var script = string.Join("; ", new[] { "gps", "network" }.Select(p => $"cmd location providers set-test-provider-location {p} --location {loc} --accuracy 5"));
        string o = ""; try { o = await d.Client.ShellAsync(script, 10); } catch { }
        // 測試提供者不見了（例如手機重開）就整組重建
        if (o.Contains("Exception") || o.Contains("not a test provider")) await ApplyAsync(d, lat, lng);
    }

    public static async Task StopAsync(Device d)
    {
        try { await d.Client.ShellAsync("cmd location providers remove-test-provider gps; cmd location providers remove-test-provider network", 10); } catch { }
    }
}

/// <summary>各裝置目前的模擬狀態。模擬由這裡負責維持（每 5 秒重送位置），跟地圖視窗開不開無關，只有按「停止模擬」才會結束。</summary>
public sealed class LocationSimulation
{
    public static LocationSimulation Shared { get; } = new();
    private readonly Dictionary<string, (double Lat, double Lng)> _active = new();
    private readonly Dictionary<string, CancellationTokenSource> _tasks = new();
    public event Action? Changed;

    public bool IsActive(Device d) => _active.ContainsKey(d.Id);
    public (double Lat, double Lng)? Coordinate(Device d) => _active.TryGetValue(d.Id, out var c) ? c : null;

    public async Task<bool> StartAsync(Device d, double lat, double lng)
    {
        if (!await LocationSimulator.ApplyAsync(d, lat, lng)) return false;
        _active[d.Id] = (lat, lng);
        _tasks.GetValueOrDefault(d.Id)?.Cancel();
        var cts = _tasks[d.Id] = new CancellationTokenSource();
        var q = DispatcherQueue.GetForCurrentThread();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(5000, cts.Token); } catch { return; }
                if (!_active.TryGetValue(d.Id, out var cur)) return;
                await LocationSimulator.RefreshAsync(d, cur.Lat, cur.Lng);
            }
        });
        q.TryEnqueue(() => Changed?.Invoke());
        return true;
    }

    public async Task StopAsync(Device d)
    {
        _tasks.GetValueOrDefault(d.Id)?.Cancel(); _tasks.Remove(d.Id);
        _active.Remove(d.Id);
        await LocationSimulator.StopAsync(d);
        Changed?.Invoke();
    }
}

/// <summary>模擬定位視窗：地圖（Leaflet＋OpenStreetMap，用 WebView2）點選或搜尋地點</summary>
public sealed class LocationSimulatorWindow : Window
{
    private readonly Device _device;
    private readonly WebView2 _web = new();
    private double? _lat, _lng;
    private readonly TextBlock _coord = Ui.Ui.Text("點地圖選擇位置", 13, true);
    private readonly TextBlock _message = Ui.Ui.Text("", 13, true);
    private readonly Button _apply = new() { Content = "套用到手機", IsEnabled = false, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _stop = new() { Content = "停止模擬", Visibility = Visibility.Collapsed };
    private readonly ComboBox _results = new() { MinWidth = 220, Visibility = Visibility.Collapsed };
    private List<(string Name, double Lat, double Lng)> _found = [];
    private static readonly HttpClient Http = new() { DefaultRequestHeaders = { { "User-Agent", "PhoneCentre/1.0 (Windows; location simulator)" } } };

    public LocationSimulatorWindow(Device device)
    {
        _device = device;
        Title = $"模擬定位 — {device.DisplayName}";
        PhoneCentre.Support.WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 720));
        _ = new WindowSubclass(this) { MinSize = () => (560, 420) };

        var search = new TextBox { PlaceholderText = "搜尋地點或地址", MinWidth = 300 };
        search.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await SearchAsync(search.Text); };
        _results.SelectionChanged += (_, _) =>
        {
            if (_results.SelectedIndex >= 0 && _results.SelectedIndex < _found.Count) { var f = _found[_results.SelectedIndex]; Choose(f.Lat, f.Lng, 16); }
        };
        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(10), Children = { new MIcon("search", 18) { Foreground = Ui.Ui.Secondary }, search, _results } };

        _apply.Click += async (_, _) =>
        {
            if (_lat is not { } la || _lng is not { } lo) return;
            _apply.IsEnabled = false;
            bool ok = await LocationSimulation.Shared.StartAsync(_device, la, lo);
            _message.Text = ok ? "已套用（關閉視窗後仍會維持模擬）" : "套用失敗（手機可能不允許測試定位）";
            _apply.IsEnabled = true; UpdateButtons();
        };
        _stop.Click += async (_, _) => { await LocationSimulation.Shared.StopAsync(_device); _message.Text = "已停止模擬，恢復真實定位"; UpdateButtons(); };
        var close = new Button { Content = "關閉" }; close.Click += (_, _) => Close();
        var bottom = new Grid { Padding = new Thickness(10), ColumnSpacing = 10 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_coord);
        Grid.SetColumn(_message, 1); bottom.Children.Add(_message);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _stop, close, _apply } };
        Grid.SetColumn(buttons, 2); bottom.Children.Add(buttons);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(_web, 1); root.Children.Add(_web);
        Grid.SetRow(bottom, 2); root.Children.Add(bottom);
        Content = root;

        if (LocationSimulation.Shared.Coordinate(device) is { } c) { _lat = c.Lat; _lng = c.Lng; _coord.Text = $"{c.Lat:0.00000}, {c.Lng:0.00000}"; }
        UpdateButtons();
        _ = InitMapAsync();
    }

    private void UpdateButtons()
    {
        _apply.IsEnabled = _lat is not null;
        _stop.Visibility = LocationSimulation.Shared.IsActive(_device) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task InitMapAsync()
    {
        try
        {
            var dir = Path.Combine(Assets.SupportDir, "map");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "leaflet.js"), ReadResource("leaflet.js"));
            File.WriteAllText(Path.Combine(dir, "leaflet.css"), ReadResource("leaflet.css"));
            File.WriteAllText(Path.Combine(dir, "index.html"), MapHtml);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Assets.SupportDir, "WebView2"), new CoreWebView2EnvironmentOptions());
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.SetVirtualHostNameToFolderMapping("map.phonecentre.local", dir, CoreWebView2HostResourceAccessKind.Allow);
            _web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                try
                {
                    using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                    var s = doc.RootElement.GetString() ?? "";
                    using var inner = JsonDocument.Parse(s);
                    _lat = inner.RootElement.GetProperty("lat").GetDouble(); _lng = inner.RootElement.GetProperty("lng").GetDouble();
                    DispatcherQueue.TryEnqueue(() => { _coord.Text = $"{_lat:0.00000}, {_lng:0.00000}"; UpdateButtons(); });
                }
                catch { }
            };
            _web.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                if (_lat is { } la && _lng is { } lo) _ = _web.CoreWebView2.ExecuteScriptAsync($"setMarker({la.ToString(CultureInfo.InvariantCulture)},{lo.ToString(CultureInfo.InvariantCulture)},15)");
            };
            _web.CoreWebView2.Navigate("https://map.phonecentre.local/index.html");
        }
        catch (Exception e)
        {
            Log.Write("開啟地圖失敗 " + e.Message);
            _message.Text = "無法開啟地圖：需要安裝 Microsoft Edge WebView2 執行階段";
        }
    }

    private static string ReadResource(string name)
    {
        using var s = typeof(LocationSimulatorWindow).Assembly.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private void Choose(double lat, double lng, int zoom)
    {
        _lat = lat; _lng = lng; _coord.Text = $"{lat:0.00000}, {lng:0.00000}"; UpdateButtons();
        _ = _web.CoreWebView2?.ExecuteScriptAsync($"setMarker({lat.ToString(CultureInfo.InvariantCulture)},{lng.ToString(CultureInfo.InvariantCulture)},{zoom})");
    }

    private async Task SearchAsync(string q)
    {
        if (q.Trim().Length == 0) return;
        try
        {
            var json = await Http.GetStringAsync($"https://nominatim.openstreetmap.org/search?format=json&limit=8&accept-language=zh-TW&q={Uri.EscapeDataString(q)}");
            using var doc = JsonDocument.Parse(json);
            _found = doc.RootElement.EnumerateArray().Select(x => (x.GetProperty("display_name").GetString() ?? "未命名",
                double.Parse(x.GetProperty("lat").GetString()!, CultureInfo.InvariantCulture), double.Parse(x.GetProperty("lon").GetString()!, CultureInfo.InvariantCulture))).ToList();
            _results.Items.Clear();
            foreach (var f in _found) _results.Items.Add(f.Name.Length > 50 ? f.Name[..50] + "…" : f.Name);
            _results.Visibility = _found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _message.Text = _found.Count == 0 ? "找不到這個地點" : $"搜尋結果 {_found.Count} 筆";
            if (_found.Count > 0) _results.SelectedIndex = 0;
        }
        catch (Exception e) { _message.Text = "搜尋失敗：" + e.Message; }
    }

    private const string MapHtml = """
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <link rel="stylesheet" href="leaflet.css"><script src="leaflet.js"></script>
        <style>html,body,#map{height:100%;margin:0;background:#1b1d22}</style></head>
        <body><div id="map"></div><script>
        var map = L.map('map').setView([25.0339, 121.5645], 12);
        L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {maxZoom: 19, attribution: '&copy; OpenStreetMap'}).addTo(map);
        var marker = null;
        function setMarker(lat, lng, zoom) {
          if (marker) marker.setLatLng([lat, lng]);
          else marker = L.circleMarker([lat, lng], {radius: 9, color: '#fff', weight: 3, fillColor: '#0a84ff', fillOpacity: 1}).addTo(map);
          if (zoom) map.setView([lat, lng], zoom);
        }
        map.on('click', function (e) {
          setMarker(e.latlng.lat, e.latlng.lng);
          window.chrome.webview.postMessage(JSON.stringify({lat: e.latlng.lat, lng: e.latlng.lng}));
        });
        </script></body></html>
        """;
}
