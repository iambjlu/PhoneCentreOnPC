using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace PhoneCentre.Pages;

/// <summary>裝置總覽（對應 DeviceOverview.swift）：標題與狀態環、喜好項目／已開啟、五個大按鈕、裝置資訊、拖放區</summary>
public sealed class OverviewPage : UserControl
{
    private readonly string _deviceId;
    private DeviceStatus? _status;
    private string _storage = "";
    private bool _storageLoaded, _showSerial;
    private string _message = "";

    private readonly StackPanel _headerHost = new(), _stripHost = new(), _infoHost = new();
    private readonly TextBlock _dropText = Ui.Ui.Text("", 14, true);
    private readonly Rectangle _dropBorder = new();
    private DispatcherQueueTimer? _statusTimer, _taskTimer;
    private AppCatalog? _catalog;

    private Device? Device => DeviceManager.Shared.Device(_deviceId);

    public OverviewPage(string deviceId)
    {
        _deviceId = deviceId;
        _status = Device is { } d ? DeviceStatusStore.Shared.StatusFor(d) : null;

        _dropText.Text = DefaultDropText;
        _dropText.TextWrapping = TextWrapping.Wrap; _dropText.TextAlignment = TextAlignment.Center;
        _dropBorder.RadiusX = _dropBorder.RadiusY = 12; _dropBorder.StrokeThickness = 2;
        _dropBorder.StrokeDashArray = [3, 3]; _dropBorder.Stroke = Ui.Ui.Hex(0x66FFFFFF);
        var dropZone = new Grid { Height = 90, AllowDrop = true, Background = Ui.Ui.Hex(0x01000000) };
        dropZone.Children.Add(_dropBorder);
        dropZone.Children.Add(new Grid { Padding = new Thickness(16), Children = { _dropText } });
        _dropText.HorizontalAlignment = HorizontalAlignment.Center; _dropText.VerticalAlignment = VerticalAlignment.Center;
        dropZone.DragOver += (_, e) => { e.AcceptedOperation = DataPackageOperation.Copy; _dropBorder.Stroke = Ui.Ui.Accent; };
        dropZone.DragLeave += (_, _) => _dropBorder.Stroke = Ui.Ui.Hex(0x66FFFFFF);
        dropZone.Drop += async (_, e) =>
        {
            _dropBorder.Stroke = Ui.Ui.Hex(0x66FFFFFF);
            if (!e.DataView.Contains(StandardDataFormats.StorageItems) || Device is not { } dev) return;
            var items = await e.DataView.GetStorageItemsAsync();
            await DropHandler.HandleAsync(items.Select(i => i.Path).ToList(), dev, m => { _message = m; _dropText.Text = m; });
        };

        var root = new StackPanel { Spacing = 20, Padding = new Thickness(24), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(_headerHost);
        root.Children.Add(_stripHost);
        root.Children.Add(BuildButtons());
        root.Children.Add(BuildInfoBox());
        root.Children.Add(dropZone);
        Content = new ScrollViewer { Content = new Grid { Children = { root }, HorizontalAlignment = HorizontalAlignment.Center }, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        RenderAll();
    }

    private string DefaultDropText => "把檔案拖到這裡：APK 會安裝、照片影片存到相機膠卷、其他存到下載";

    // MARK: 生命週期

    private void Start()
    {
        DeviceManager.Shared.Changed += OnDevices;
        DeviceStatusStore.Shared.Changed += OnStatus;
        if (Device is { } d)
        {
            _catalog = DeviceManager.Shared.Catalog(d);
            _catalog.Changed += RenderStrip;
            _ = LoadCatalogAsync();
        }
        var q = DispatcherQueue.GetForCurrentThread();
        _statusTimer = q.CreateTimer(); _statusTimer.Interval = TimeSpan.FromSeconds(10);
        _statusTimer.Tick += (_, _) => _ = LoadStatusAsync();
        _statusTimer.Start();
        _taskTimer = q.CreateTimer(); _taskTimer.Interval = TimeSpan.FromSeconds(3);
        _taskTimer.Tick += (_, _) => { if (Device is not null && _catalog is not null) _ = _catalog.RefreshTasksAsync(); };
        _taskTimer.Start();
        _ = LoadStatusAsync();
    }

    private void Stop()
    {
        DeviceManager.Shared.Changed -= OnDevices;
        DeviceStatusStore.Shared.Changed -= OnStatus;
        if (_catalog is not null) _catalog.Changed -= RenderStrip;
        _statusTimer?.Stop(); _taskTimer?.Stop();
    }

    private async Task LoadCatalogAsync()
    {
        if (_catalog is null) return;
        if (_catalog.Apps.Count == 0) await _catalog.ReloadAsync();
        await _catalog.RefreshTasksAsync();
    }

    private void OnDevices() { RenderHeader(); RenderInfo(); }

    private void OnStatus()
    {
        if (Device is { } d && DeviceStatusStore.Shared.StatusFor(d) is { } s) { _status = s; RenderHeader(); RenderInfo(); }
    }

    private async Task LoadStatusAsync()
    {
        if (Device is not { } d) return;
        var c = d.Client;
        var s = await Task.Run(() => DeviceStatus.FetchAsync(c));
        if (s is not null) { _status = s; RenderHeader(); RenderInfo(); }
        // df -k：總量、已用、可用（KB）。顯示「剩餘可用空間」與占總容量的百分比
        try
        {
            var df = await Task.Run(() => c.ShellAsync("df -k /data | tail -1"));
            var p = df.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 4 && double.TryParse(p[1], out var total) && double.TryParse(p[2], out var used) && double.TryParse(p[3], out var avail) && total > 0)
            {
                static string G(double kb) => $"{kb / 1_048_576:0}G";
                _storage = $"已用 {G(used)} / 共 {G(total)}，剩餘可用空間 {G(avail)}（{Math.Round(avail / total * 100):0}%）";
                _storageLoaded = true;
                RenderInfo();
            }
        }
        catch { }
    }

    // MARK: 畫面

    private void RenderAll() { RenderHeader(); RenderStrip(); RenderInfo(); }

    private void RenderHeader()
    {
        _headerHost.Children.Clear();
        if (Device is not { } d) return;
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var phone = new Button { Content = new Ui.MIcon("smartphone", 56) { Foreground = Ui.Ui.Secondary }, Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(0) };
        ToolToolTip(phone, "開啟鏡像畫面");
        phone.Click += (_, _) => WindowManager.OpenMirror(d);
        grid.Children.Add(phone);

        var texts = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = d.DisplayName, FontSize = 28, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        texts.Children.Add(Ui.Ui.Text(d.Subtitle, 14, true));
        texts.Children.Add(Ui.Ui.Text(d.IsRemote ? "透過遠端 PhoneCentre (Beta)" : d.IsWireless ? $"無線 · {d.Serial}" : $"USB · {d.Serial}", 12, true));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        // 跟鏡像視窗同款的狀態圖；點一下開鏡像畫面
        var ind = new Ui.StatusIndicator(52);
        ind.Set(_status);
        ind.Tapped += (_, _) => WindowManager.OpenMirror(d);
        Grid.SetColumn(ind, 2);
        grid.Children.Add(ind);
        _headerHost.Children.Add(grid);
    }

    private static void ToolToolTip(UIElement e, string tip) => ToolTipService.SetToolTip(e, tip);

    private void RenderStrip()
    {
        _stripHost.Children.Clear();
        if (Device is not { } d || _catalog is null) return;
        var (favorites, openOnly) = _catalog.Sections(AppSettings.Pinned(d.Key));
        _stripHost.Spacing = 14;
        StripRow("喜好項目", favorites, d);
        StripRow("已開啟", openOnly, d);
    }

    private void StripRow(string title, List<string> packages, Device d)
    {
        if (packages.Count == 0 || _catalog is null) return;
        var items = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Padding = new Thickness(0, 4, 0, 4) };
        foreach (var pkg in packages) items.Children.Add(AppItem(pkg, d));
        var scroll = new ArrowScroll(items);
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(Ui.Ui.Text(title, 15, true, true));
        col.Children.Add(scroll);
        _stripHost.Children.Add(col);
    }

    private FrameworkElement AppItem(string pkg, Device d)
    {
        var catalog = _catalog!;
        var item = new AppCell(catalog, pkg, catalog.Label(pkg), 52, 72);
        item.Tapped += (_, _) => WindowManager.Launch(d, pkg);
        // 選單順序與 App 列相同：強制停止、在獨立視窗開啟、（分隔線）喜好項目
        var menu = new MenuFlyout();
        var stop = new MenuFlyoutItem { Text = "強制停止" }; stop.Click += async (_, _) => await catalog.ForceStopAsync(pkg);
        var openWin = new MenuFlyoutItem { Text = "在獨立視窗開啟" }; openWin.Click += (_, _) => WindowManager.OpenApp(d, pkg);
        var pin = new MenuFlyoutItem { Text = AppSettings.IsPinned(pkg, d.Key) ? "移除喜好項目" : "加入喜好項目" };
        pin.Click += (_, _) => { AppSettings.TogglePin(pkg, d.Key); RenderStrip(); };
        menu.Items.Add(stop); menu.Items.Add(openWin); menu.Items.Add(new MenuFlyoutSeparator()); menu.Items.Add(pin);
        item.ContextFlyout = menu;
        return item;
    }

    private FrameworkElement BuildButtons()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(BigButton("鏡像畫面", "cast", () => { if (Device is { } d) WindowManager.OpenMirror(d); }));
        row.Children.Add(BigButton("桌面模式(Beta)", "desktop_windows", () => Toast("桌面模式尚未移植到 Windows 版")));
        row.Children.Add(BigButton("安裝 APK…", "inventory_2", PickApk));
        row.Children.Add(BigButton("解鎖密碼…", "fingerprint", () => Toast("解鎖密碼尚未移植到 Windows 版")));
        row.Children.Add(BigButton("重新啟動…", "restart_alt", ConfirmReboot));
        return new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static Button BigButton(string title, string icon, Action click)
    {
        var b = new Button
        {
            Width = 110, Height = 70, Padding = new Thickness(0),
            Content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Children = { new Ui.MIcon(icon, 24) { HorizontalAlignment = HorizontalAlignment.Center }, new TextBlock { Text = title, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center } } },
        };
        b.Click += (_, _) => click();
        return b;
    }

    private FrameworkElement BuildInfoBox()
    {
        var box = new Border
        {
            Background = Ui.Ui.Hex(0x0DFFFFFF), BorderBrush = Ui.Ui.Hex(0x1AFFFFFF), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12),
        };
        var col = new StackPanel { Spacing = 8 };
        col.Children.Add(Ui.Ui.Text("裝置資訊", 14, true, true));
        col.Children.Add(_infoHost);
        box.Child = col;
        return box;
    }

    private void RenderInfo()
    {
        _infoHost.Children.Clear();
        _infoHost.Spacing = 6;
        if (Device is not { } d) return;
        var i = d.Info;
        Row("型號", i is null ? d.Model : $"{i.Manufacturer} {i.Model}");
        Row("Android", i is null ? "—" : $"{i.AndroidVersion}（API {i.Sdk}）");
        Row("螢幕", i is null ? "—" : $"{i.ScreenSize} · {i.Density} dpi");
        var s = _status;
        if (s is null)
        {
            foreach (var k in new[] { "生產日期", "電信業者", "Wi-Fi", "電池", "電池容量", "循環次數" }) LoadingRow(k);
        }
        else
        {
            if (ManufactureText(s) is { } md) Row("生產日期", md);
            if (s.Sims.Count > 1) foreach (var sim in s.Sims) Row($"電信業者{sim.Slot}", SimText(sim));
            else Row("電信業者", CarrierText(s));
            Row("Wi-Fi", s.WifiSSID.Length == 0 ? "未連線" : s.WifiIP.Length == 0 ? s.WifiSSID : $"{s.WifiSSID} · {s.WifiIP}");
            Row("電池", BatteryText(s));
            if (CapacityText(s) is { } cap) Row("電池容量", cap);
            if (s.BatteryCycles is { } n) Row("循環次數", $"{n} 次");
        }
        if (_storageLoaded) Row("儲存空間", _storage.Length == 0 ? "—" : _storage); else LoadingRow("儲存空間");

        var serial = new TextBlock { Text = _showSerial ? (i?.SerialNo is { Length: > 0 } sn ? sn : d.Serial) : "••••••••••", IsTextSelectionEnabled = true };
        var eye = new Button { Content = new Ui.MIcon(_showSerial ? "visibility_off" : "visibility", 16) { Foreground = Ui.Ui.Secondary }, Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
        ToolToolTip(eye, _showSerial ? "隱藏序號" : "顯示序號");
        eye.Click += (_, _) => { _showSerial = !_showSerial; RenderInfo(); };
        _infoHost.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { LabelBlock("序號"), serial, eye },
        });
    }

    private static TextBlock LabelBlock(string k) => new() { Text = k, Foreground = Ui.Ui.Secondary, Width = 80, VerticalAlignment = VerticalAlignment.Center };

    private void Row(string k, string v) => _infoHost.Children.Add(new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { LabelBlock(k), new TextBlock { Text = v, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap } },
    });

    private void LoadingRow(string k) => _infoHost.Children.Add(new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { LabelBlock(k), new ProgressRing { Width = 14, Height = 14, IsActive = true } },
    });

    private static string? ManufactureText(DeviceStatus s)
    {
        var d = s.ManufactureDate;
        return d.Length == 8 && d.All(char.IsDigit) ? $"{d[..4]}/{d.Substring(4, 2)}/{d[6..]}" : null;
    }

    /// <summary>不管 SIM 卡是哪家，只寫目前收到的電信：正常服務「中華電信 LTE -106dBm」；只能撥緊急電話「僅限緊急通話 LTE -106dBm (中華電信)」</summary>
    private static string SimText(SimInfo sim)
    {
        if (sim.PowerOff) return "行動網路已關閉";
        var signal = new[] { sim.Tech, sim.Dbm is { } dbm ? $"{dbm}dBm" : "" }.Where(x => x.Length > 0).ToList();
        if (sim.EmergencyOnly && !sim.InService)
        {
            var head = string.Join(" ", new[] { "僅限緊急通話" }.Concat(signal));
            return sim.Carrier.Length == 0 ? head : $"{head} ({sim.Carrier})";
        }
        var parts = (sim.Carrier.Length == 0 ? [] : new List<string> { sim.Carrier }).Concat(signal).ToList();
        return parts.Count == 0 ? (sim.InService ? "—" : "沒有服務") : string.Join(" ", parts);
    }

    private static string CarrierText(DeviceStatus s) => !s.HasSim ? "沒有 SIM 卡"
        : SimText(s.Sims.FirstOrDefault() ?? new SimInfo { Carrier = s.Carrier, Tech = s.RadioTech, Dbm = s.SignalDbm, InService = s.InService });

    /// <summary>插著電不一定在充電（例如最佳化電池充電），用電池狀態碼區分</summary>
    private static string BatteryText(DeviceStatus s)
    {
        if (s.BatteryLevel < 0) return "—";
        var t = $"{s.BatteryLevel}%";
        switch (s.BatteryStatus)
        {
            case 2: t += "（充電中）"; break;
            case 5: t += "（已充飽）"; break;
            case 4: if (s.PluggedIn) t += "（已插電，未充電）"; break;
            default: if (s.PluggedIn) t += "（已插電，未充電）"; break;
        }
        return t;
    }

    private static string? CapacityText(DeviceStatus s)
    {
        if (s.BatteryMaxMah is not { } max) return null;
        if (s.BatteryDesignMah is not { } design) return $"{max} mAh";
        return $"{max} mAh / 設計 {design} mAh（{Math.Round((double)max / design * 100):0}%）";
    }

    // MARK: 動作

    private void Toast(string text) { _message = text; _dropText.Text = text; }

    private async void PickApk()
    {
        if (Device is not { } d) return;
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".apk");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;
        await DropHandler.HandleAsync(files.Select(f => f.Path).ToList(), d, m => Toast(m));
    }

    /// <summary>重新啟動手機：先確認，再用 svc power reboot（shell 有權限；不行再試 reboot）</summary>
    private async void ConfirmReboot()
    {
        if (Device is not { } d) return;
        var dialog = new ContentDialog
        {
            Title = $"要重新啟動「{d.DisplayName}」嗎？",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "手機會立刻重新開機，開啟中的鏡像與獨立視窗都會中斷。重新開機後如果需要輸入 SIM 卡 PIN 或解鎖，請在手機上操作；無線連線要等手機重新連上 Wi-Fi 才會恢復。" },
            PrimaryButtonText = "重新啟動", CloseButtonText = "取消", XamlRoot = XamlRoot,
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Toast("正在重新啟動…");
        var c = d.Client;
        _ = Task.Run(async () => { try { await c.ShellAsync("svc power reboot 2>/dev/null || reboot", 8); } catch { } });
    }
}
