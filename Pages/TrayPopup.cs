using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhoneCentre.Adb;
using PhoneCentre.Mirror;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.Graphics;

namespace PhoneCentre;

/// <summary>系統匣的快速取用視窗（對應 MenuBarView）：每台已連線裝置一區（名稱、型號、鏡像按鈕），下面一排 App 圖示（已開啟的有標記、最近使用的沒有），底部是打開 PhoneCentre、設定、結束</summary>
public sealed class TrayPopup : Window
{
    private readonly StackPanel _root = new() { Width = 360 };
    private DateTime _hiddenAt = DateTime.MinValue;
    private bool _visible;

    public TrayPopup()
    {
        Title = "PhoneCentre";
        SystemBackdrop = new MicaBackdrop();
        Content = new Grid { Children = { _root } };
        var p = OverlappedPresenter.Create();
        p.SetBorderAndTitleBar(false, false); p.IsAlwaysOnTop = true; p.IsResizable = false; p.IsMaximizable = false; p.IsMinimizable = false;
        AppWindow.SetPresenter(p);
        AppWindow.IsShownInSwitchers = false;
        Activated += (_, e) => { if (e.WindowActivationState == WindowActivationState.Deactivated) HidePopup(); };
        DeviceManager.Shared.Changed += () => { if (_visible) DispatcherQueue.TryEnqueue(Build); };
    }

    public void Toggle()
    {
        if (_visible) { HidePopup(); return; }
        if ((DateTime.Now - _hiddenAt).TotalMilliseconds < 300) return;   // 點圖示本身造成的失去焦點，不要馬上又開起來
        Build();
        Place();
        AppWindow.Show();
        Activate();
        _visible = true;
    }

    private void HidePopup()
    {
        if (!_visible) return;
        _visible = false; _hiddenAt = DateTime.Now;
        AppWindow.Hide();
    }

    /// <summary>放在游標（系統匣圖示）附近，不超出工作區</summary>
    private void Place()
    {
        _root.Measure(new Windows.Foundation.Size(360, double.PositiveInfinity));
        var c = DragGhost.CursorPosition;
        var area = DisplayArea.GetFromPoint(new PointInt32(c.X, c.Y), DisplayAreaFallback.Nearest).WorkArea;
        double s = Content.XamlRoot?.RasterizationScale ?? GetScale();
        int w = (int)(360 * s), h = (int)(Math.Min(_root.DesiredSize.Height, 700) * s) + 2;
        int x = Math.Clamp(c.X - w / 2, area.X + 8, area.X + area.Width - w - 8);
        int y = c.Y > area.Y + area.Height / 2 ? c.Y - h - 12 : c.Y + 12;
        y = Math.Clamp(y, area.Y + 8, area.Y + area.Height - h - 8);
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private double GetScale()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        return GetDpiForWindow(hwnd) / 96.0;
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);

    private void Build()
    {
        _root.Children.Clear();
        var devices = DeviceManager.Shared.OnlineDevices.ToList();
        if (devices.Count == 0) { var t = Ui.Ui.Text("沒有已連線的裝置", 14, true); t.Margin = new Thickness(16); _root.Children.Add(t); }
        foreach (var d in devices)
        {
            _root.Children.Add(DeviceSection(d));
            _root.Children.Add(Ui.Ui.Divider(0.15));
        }
        var open = new Button { Content = "打開 PhoneCentre", Background = null, BorderThickness = new Thickness(0) };
        open.Click += (_, _) => { HidePopup(); App.ShowMainWindow(); };
        var gear = new Button { Content = new MIcon("settings", 18), Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        ToolTipService.SetToolTip(gear, "設定");
        gear.Click += (_, _) => { HidePopup(); WindowManager.OpenSettings(); };
        var power = new Button { Content = new MIcon("power_settings_new", 18), Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(8) };
        ToolTipService.SetToolTip(power, "結束 PhoneCentre");
        power.Click += (_, _) => { HidePopup(); App.Quit(); };
        var row = new Grid { Padding = new Thickness(12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(open);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { gear, power } };
        Grid.SetColumn(right, 1); row.Children.Add(right);
        _root.Children.Add(row);
        _root.UpdateLayout();
        if (_visible) Place();
    }

    private FrameworkElement DeviceSection(Device d)
    {
        var catalog = DeviceManager.Shared.Catalog(d);
        var col = new StackPanel { Spacing = 8, Padding = new Thickness(12) };
        var head = new Grid { ColumnSpacing = 10 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 9, Height = 9, Fill = Ui.Ui.Hex(0xFF30D158), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0) };
        var texts = new StackPanel { Children = { Ui.Ui.Text(d.DisplayName, 15, false, true), Ui.Ui.Text(d.Subtitle.Length == 0 ? d.Serial : d.Subtitle, 12, true) } };
        var mirror = new Button { Content = new MIcon("cast", 18), Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(6) };
        ToolTipService.SetToolTip(mirror, "鏡像畫面");
        mirror.Click += (_, _) => { HidePopup(); WindowManager.OpenMirror(d); };
        head.Children.Add(dot); Grid.SetColumn(texts, 1); head.Children.Add(texts); Grid.SetColumn(mirror, 2); head.Children.Add(mirror);
        col.Children.Add(head);

        var apps = catalog.Running.Concat(catalog.Recent).Distinct().Take(14).ToList();
        if (apps.Count > 0)
        {
            var grid = new ItemsRepeater
            {
                Margin = new Thickness(17, 0, 0, 0),
                Layout = new UniformGridLayout { MinItemWidth = 38, MinItemHeight = 44, MinColumnSpacing = 6, MinRowSpacing = 8, ItemsJustification = UniformGridLayoutItemsJustification.Start },
                ItemsSource = apps.Select(pkg =>
                {
                    var cell = new StackPanel { Spacing = 3, Background = Ui.Ui.Hex(0x01000000) };
                    var icon = Ui.Ui.AppIcon(catalog, pkg, 32); icon.HorizontalAlignment = HorizontalAlignment.Center;
                    cell.Children.Add(icon);
                    cell.Children.Add(Ui.Ui.RunningMark(catalog.Running.Contains(pkg), pkg == catalog.Foreground));
                    ToolTipService.SetToolTip(cell, catalog.Label(pkg));
                    cell.Tapped += (_, _) => { HidePopup(); WindowManager.Launch(d, pkg); };
                    return (UIElement)cell;
                }).ToList(),
            };
            col.Children.Add(grid);
        }
        catalog.Changed -= OnCat; catalog.Changed += OnCat;
        if (catalog.Apps.Count == 0 && !catalog.Loading) _ = catalog.ReloadAsync();
        else if (!_refreshing.Contains(d.Id)) { _refreshing.Add(d.Id); _ = catalog.RefreshTasksAsync().ContinueWith(_ => _refreshing.Remove(d.Id)); }
        return col;
    }

    private readonly HashSet<string> _refreshing = new();
    private void OnCat() { if (_visible) DispatcherQueue.TryEnqueue(Build); }
}
