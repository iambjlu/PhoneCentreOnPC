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
    private double _size;
    private DeviceStatus? _last;
    /// <summary>鏡像視窗裡的狀態環（底部橫條、側邊欄）：有閃電時數字與閃電各往外 0.5px（共拉開 1px），上方缺口跟著加寬</summary>
    private readonly bool _mirror;
    private readonly Canvas _canvas;
    private readonly ProgressRing _spinner;

    public StatusIndicator(double size = 38, bool mirror = false)
    {
        _size = size;
        _mirror = mirror;
        IsTabStop = false;
        Width = Height = size;
        _canvas = new Canvas { Width = size, Height = size };
        _spinner = new ProgressRing { Width = 16, Height = 16, IsActive = true, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Grid { Children = { _canvas, _spinner } };
        Set(null);
    }

    /// <summary>線寬、點、內縮這些固定值是照 38 調的；比 38 大（例如總覽的 52）時按比例放大，環的粗細比例才不會變細。使用處的尺寸不要小於 38</summary>
    private double K => Math.Max(_size, 38) / 38;
    private double LineW => 3.2 * K;
    /// <summary>環的半徑（中心線）</summary>
    private double RingR => _size / 2 - 2 * K;
    /// <summary>大圖（總覽的 52）：閃電與數字的微調跟小圖（側邊欄、裝置清單、底部橫條，38～40）不同</summary>
    private bool LargeSize => _size >= 46;
    /// <summary>環裡兩個文字（電量數字與 4G／5G）共用的字級：不隨位數或閃電改變；上方缺口（<see cref="TopHalfGap"/>）寬到放得下三位數與閃電</summary>
    private double LevelFontSize => Math.Min(12 * K, _size * 0.3) * 0.9;
    /// <summary>上方缺口的半角度（離正上方 270° 的角度）：放得下三位數／閃電，字級才能跟兩位數一樣大</summary>
    private double TopHalfGap => _mirror ? 44.3 : 42;
    private (double From, double To)[] Segments => [(145, 270 - TopHalfGap), (270 + TopHalfGap, 395)];

    /// <summary>世代文字的垂直位置：跟電量數字對稱——離下方圓頭的距離等於電量數字離上方圓頭的距離</summary>
    private double TechOffsetY
    {
        get
        {
            double r = RingR;
            double topGap = (r - 0.8 * K) - r * Math.Cos(TopHalfGap * Math.PI / 180);
            return r * Math.Sin(35 * Math.PI / 180) + topGap;
        }
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

    private TextBlock AddText(string text, double fontSize, Brush brush, double centerX, double centerY, int characterSpacing = 0)
    {
        var t = new TextBlock { Text = text, FontSize = fontSize, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = brush, CharacterSpacing = characterSpacing };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(t, centerX - t.DesiredSize.Width / 2);
        Canvas.SetTop(t, centerY - t.DesiredSize.Height / 2);
        _canvas.Children.Add(t);
        return t;
    }

    /// <summary>同一個控制項在直向側邊欄（38）與橫向底部橫條（40）之間共用，換版面時改尺寸並重畫</summary>
    public void Resize(double size)
    {
        if (Math.Abs(size - _size) < 0.01) return;
        _size = size;
        Width = Height = _canvas.Width = _canvas.Height = size;
        Set(_last);
    }

    /// <summary>null＝還沒讀到狀態（只轉圈圈）</summary>
    public void Set(DeviceStatus? s)
    {
        _last = s;
        _canvas.Children.Clear();
        _spinner.Visibility = s is null ? Visibility.Visible : Visibility.Collapsed;
        if (s is null) return;

        double cx = _size / 2, cy = _size / 2, r = RingR, lineW = LineW, k = K;
        double level = s.BatteryLevel < 0 ? 0 : s.BatteryLevel / 100.0;
        Brush ring = s.Charging ? new SolidColorBrush(Color.FromArgb(255, 52, 199, 89))
            : s.BatteryLevel is >= 0 and <= 20 ? new SolidColorBrush(Color.FromArgb(255, 255, 69, 58)) : White();
        var segments = Segments;
        foreach (var (from, to) in segments) _canvas.Children.Add(Arc(cx, cy, r, from, to, White(0.22), lineW));
        // 電量依序填滿兩段（左段先、右段後）
        double remain = segments.Sum(x => x.To - x.From) * level;
        foreach (var (from, to) in segments)
        {
            if (remain <= 0) break;
            double len = Math.Min(remain, to - from);
            _canvas.Children.Add(Arc(cx, cy, r, from, from + len, ring, lineW));
            remain -= len;
        }
        // 訊號格：畫在同一個圓上，彼此與兩端離電池環的角度都相同（22°）
        // 未啟用 Wi-Fi 時，訊號改成畫在中間（長條＋世代文字），環上不畫點
        if (s.HasSim && s.Wifi)
        {
            for (int i = 0; i < 4; i++)
            {
                var pt = At(cx, cy, r, 90 + (1.5 - i) * 22);
                double d = 3.6 * k;
                var dot = new Ellipse { Width = d, Height = d, Fill = White(i < s.SignalBars ? 1 : 0.28) };
                Canvas.SetLeft(dot, pt.X - d / 2); Canvas.SetTop(dot, pt.Y - d / 2);
                _canvas.Children.Add(dot);
            }
        }
        if (!s.Wifi) DrawBars(s, cx, cy);
        if (s.BatteryLevel >= 0) DrawLevel(s, cx, cy - r + 0.8 * k, ring);
        // 中間：Wi-Fi 圖示（依格數）；行動網路的世代文字放在環下方缺口，也就是 Wi-Fi 時訊號點的位置
        if (s.Wifi) DrawWifi(cx, cy, s.WifiLevel);
        else if (s.HasSim && !s.NoService && s.CellularTech is { } tech)
            AddText(tech, LevelFontSize, White(), cx, cy + TechOffsetY);
        ToolTipService.SetToolTip(this, s.Summary);
    }

    /// <summary>
    /// 電量數字；插著電（充電中或插電未充電）時右邊多一個閃電（充電中＝綠色、插電沒在充＝灰色）。
    /// 閃電等於多一個字元，佔「第三位數」的位置，版面（字級、位置）跟三位數一樣；電量 100% 本來就三位數，不顯示閃電
    /// </summary>
    private void DrawLevel(DeviceStatus s, double cx, double centerY, Brush ring)
    {
        double fs = LevelFontSize;
        bool bolt = s.BatteryLevel < 100 && (s.Charging || s.PluggedIn);
        // 三位數（含閃電）的字距收緊 0.08 倍字級；字距負值會讓文字框右邊少一塊、墨跡偏右，另外往左補償一半
        double tracking = bolt || s.BatteryLevel >= 100 ? -0.08 * fs : 0;
        var t = new TextBlock
        {
            Text = s.BatteryLevel.ToString(), FontSize = fs, FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = s.Charging ? ring : White(), CharacterSpacing = (int)Math.Round(tracking / fs * 1000),
        };
        t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = t.DesiredSize.Width + tracking;
        double boltSlot = bolt ? fs * 0.40 : 0, spacing = bolt ? -0.04 * fs : 0;
        double groupLeft = cx + tracking / 2 - (bolt ? 0.06 * fs : 0) - (textW + spacing + boltSlot) / 2;
        // 小圖：有閃電時數字右移 1px（閃電不動）；鏡像視窗只右移 0.5px（數字相對往左 0.5px）
        double digitDx = bolt && !LargeSize ? (_mirror ? 0.5 : 1) : 0;
        Canvas.SetLeft(t, groupLeft + digitDx);
        Canvas.SetTop(t, centerY - t.DesiredSize.Height / 2);
        _canvas.Children.Add(t);
        if (!bolt) return;
        // 大圖：閃電右移 1px（數字不動）；鏡像視窗閃電右移 0.5px
        double boltDx = LargeSize ? 1 : (_mirror ? 0.5 : 0);
        double iconSize = fs * 1.15;   // Material 的閃電只佔圖示方框中間約 0.42 寬，放大方框讓字形高度接近 SF Symbol
        var icon = new MIcon("bolt", iconSize) { Foreground = s.Charging ? ring : White(0.7) };
        Canvas.SetLeft(icon, groupLeft + textW + spacing + boltDx - iconSize * 0.3);   // 字形靠左貼著數字
        Canvas.SetTop(icon, centerY - iconSize / 2);
        _canvas.Children.Add(icon);
    }

    /// <summary>
    /// 中間的訊號條：4 根粗細一致的膠囊、底部對齊；外框（寬 0.42、高 0.315）跟 Wi-Fi 圖示的範圍、中心一致（單位為環直徑）。
    /// 有訊號：由低到高 4 格（高度比 0.4／0.6／0.8／1），亮起的格數依訊號、其餘為暗色（約 30% 白）；
    /// 沒有 SIM／真正沒有服務：4 根等高的平條（全暗），底部與階梯條對齊
    /// </summary>
    private void DrawBars(DeviceStatus s, double cx, double cy)
    {
        bool flat = !s.HasSim || s.NoService;
        int lit = flat ? 0 : s.SignalBars;
        double[] ratios = flat ? [1, 1, 1, 1] : [0.4, 0.6, 0.8, 1];
        double full = _size * (flat ? 0.24 : 0.315);
        double w = _size * 0.072, gap = _size * 0.044;
        double total = 4 * w + 3 * gap;
        double x0 = cx - total / 2;
        double bottom = cy - _size * 0.032 + _size * 0.315 / 2;
        for (int i = 0; i < 4; i++)
        {
            double h = Math.Max(w, full * ratios[i]);
            var bar = new Rectangle { Width = w, Height = h, RadiusX = w / 2, RadiusY = w / 2, Fill = White(i < lit ? 1 : 0.3) };
            Canvas.SetLeft(bar, x0 + i * (w + gap)); Canvas.SetTop(bar, bottom - h);
            _canvas.Children.Add(bar);
        }
    }

    /// <summary>Material 的 Wi-Fi 圖示：底下先放一個淡的完整圖示，上面再疊目前格數的亮圖示（1 格＝一個點、2 格＝點加一道弧、3 格以上＝完整）</summary>
    private void DrawWifi(double cx, double cy, int level)
    {
        double size = _size * 0.46;
        double left = cx - size / 2, top = cy - size / 2 + _size * 0.03;
        var dim = new MIcon("wifi", size) { Foreground = White(0.28) };
        Canvas.SetLeft(dim, left); Canvas.SetTop(dim, top);
        _canvas.Children.Add(dim);
        string? bright = level switch { <= 0 => null, 1 => "wifi_1_bar", 2 => "wifi_2_bar", _ => "wifi" };
        if (bright is null) return;
        var on = new MIcon(bright, size) { Foreground = White() };
        Canvas.SetLeft(on, left); Canvas.SetTop(on, top);
        _canvas.Children.Add(on);
    }
}
