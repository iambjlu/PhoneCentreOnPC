using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Services;
using Windows.Foundation;
using Color = Windows.UI.Color;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace PhoneCentre.Ui;

/// <summary>
/// 類似新版 iPhone 的狀態指示：外環＝電池（左右兩段，上方留給數字），中間＝Wi-Fi 或行動網路世代，
/// 下方的點＝主要 SIM 卡訊號格（對應 NavBar.swift 的 StatusIndicator，永遠是白色系，用在深色底）
/// </summary>
public sealed class StatusIndicator : UserControl
{
    private readonly double _size;
    private readonly Canvas _canvas;
    private readonly ProgressRing _spinner;
    private static readonly (double From, double To)[] Segments = [(145, 232), (308, 395)];

    public StatusIndicator(double size = 38)
    {
        _size = size;
        IsTabStop = false;
        Width = Height = size;
        _canvas = new Canvas { Width = size, Height = size };
        _spinner = new ProgressRing { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Grid { Children = { _canvas, _spinner } };
        Set(null);
    }

    private static Brush White(double a = 1) => new SolidColorBrush(Color.FromArgb((byte)(255 * a), 255, 255, 255));

    private static Point At(double cx, double cy, double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(cx + r * Math.Cos(a), cy + r * Math.Sin(a));
    }

    private static Path Arc(double cx, double cy, double r, double from, double to, Brush brush, double thickness)
    {
        var fig = new PathFigure { StartPoint = At(cx, cy, r, from), IsClosed = false };
        fig.Segments.Add(new ArcSegment { Point = At(cx, cy, r, to), Size = new Size(r, r), SweepDirection = SweepDirection.Clockwise, IsLargeArc = to - from > 180 });
        return new Path
        {
            Data = new PathGeometry { Figures = { fig } }, Stroke = brush, StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        };
    }

    /// <summary>null＝還沒讀到狀態（只轉圈圈）</summary>
    public void Set(DeviceStatus? s)
    {
        _canvas.Children.Clear();
        _spinner.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        if (s is null) return;

        double cx = _size / 2, cy = _size / 2, r = _size / 2 - 2;
        double level = s.BatteryLevel < 0 ? 0 : s.BatteryLevel / 100.0;
        Brush ring = s.Charging ? new SolidColorBrush(Color.FromArgb(255, 52, 199, 89))
            : s.BatteryLevel is >= 0 and <= 20 ? new SolidColorBrush(Color.FromArgb(255, 255, 69, 58)) : White();
        foreach (var (from, to) in Segments) _canvas.Children.Add(Arc(cx, cy, r, from, to, White(0.22), 3.2));
        // 電量依序填滿兩段（左段先、右段後）
        double remain = Segments.Sum(x => x.To - x.From) * level;
        foreach (var (from, to) in Segments)
        {
            if (remain <= 0) break;
            double len = Math.Min(remain, to - from);
            _canvas.Children.Add(Arc(cx, cy, r, from, from + len, ring, 3.2));
            remain -= len;
        }
        // 訊號格：畫在同一個圓上，彼此與兩端離電池環的角度都相同（22°）
        if (s.HasSim)
        {
            for (int i = 0; i < 4; i++)
            {
                var pt = At(cx, cy, r, 90 + (1.5 - i) * 22);
                const double d = 3.6;
                var dot = new Ellipse { Width = d, Height = d, Fill = White(i < s.SignalBars ? 1 : 0.28) };
                Canvas.SetLeft(dot, pt.X - d / 2); Canvas.SetTop(dot, pt.Y - d / 2);
                _canvas.Children.Add(dot);
            }
        }
        if (s.BatteryLevel >= 0)
        {
            var t = new TextBlock
            {
                Text = s.BatteryLevel.ToString(), FontSize = Math.Min(12, _size * 0.3), FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = s.Charging ? ring : White(),
            };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, cx - t.DesiredSize.Width / 2);
            Canvas.SetTop(t, 2.8 - t.DesiredSize.Height / 2);   // 壓在環頂端缺口的正中間
            _canvas.Children.Add(t);
        }
        // 中間：Wi-Fi（依格數）或 4G/5G
        if (s.Wifi) DrawWifi(cx, cy, s.WifiLevel);
        else if (s.CellularTech is { } tech)
        {
            var t = new TextBlock { Text = tech, FontSize = _size * 0.38, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = White() };
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(t, cx - t.DesiredSize.Width / 2); Canvas.SetTop(t, cy - t.DesiredSize.Height / 2);
            _canvas.Children.Add(t);
        }
        else
        {
            var icon = new MIcon("wifi_off", _size * 0.46) { Foreground = White(0.5) };
            Canvas.SetLeft(icon, cx - _size * 0.23); Canvas.SetTop(icon, cy - _size * 0.23);
            _canvas.Children.Add(icon);
        }
        ToolTipService.SetToolTip(this, s.Summary);
    }

    private void DrawWifi(double cx, double cy, int level)
    {
        double u = _size * 0.46;
        double ox = cx, oy = cy + u * 0.32;
        int arcs = (int)Math.Ceiling(level * 3 / 4.0);
        var dot = new Ellipse { Width = u * 0.2, Height = u * 0.2, Fill = White() };
        Canvas.SetLeft(dot, ox - u * 0.1); Canvas.SetTop(dot, oy - u * 0.1);
        _canvas.Children.Add(dot);
        double[] radii = [0.42, 0.74, 1.06];
        for (int i = 0; i < 3; i++)
            _canvas.Children.Add(Arc(ox, oy, u * radii[i], 228, 312, White(i < arcs ? 1 : 0.28), u * 0.14));
    }
}
