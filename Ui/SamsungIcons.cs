using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace PhoneCentre.Ui;

/// <summary>One UI 的三個系統按鍵：返回「&lt;」、主畫面「圓角方形」、多工「|||」，加上強制停止（六角形加叉）。顏色跟著 Foreground。</summary>
public sealed class SamsungIcon : UserControl
{
    public enum Kind { Back, Home, Recents, Stop }

    private readonly List<Shape> _strokes = [];
    private readonly List<Shape> _fills = [];

    public SamsungIcon(Kind kind)
    {
        IsTabStop = false;
        Width = Height = 23;                         // 20 × 1.15（跟 Mac 版一樣放大 1.15 倍）
        var canvas = new Canvas { Width = 20, Height = 20 };
        switch (kind)
        {
            case Kind.Back:
                Stroke(canvas, new Polyline { Points = [new Point(13.5, 3), new Point(6.5, 10), new Point(13.5, 17)] }, 1.9);
                break;
            case Kind.Home:
                var r = new Rectangle { Width = 14, Height = 14, RadiusX = 5, RadiusY = 5 };
                Canvas.SetLeft(r, 3); Canvas.SetTop(r, 3);
                Stroke(canvas, r, 1.9);
                break;
            case Kind.Recents:
                for (int i = 0; i < 3; i++)
                {
                    var bar = new Rectangle { Width = 1.9, Height = 14, RadiusX = 0.95, RadiusY = 0.95 };
                    Canvas.SetLeft(bar, 10 - (1.9 * 3 + 4.2 * 2) / 2 + i * (1.9 + 4.2)); Canvas.SetTop(bar, 3);
                    canvas.Children.Add(bar); _fills.Add(bar);
                }
                break;
            case Kind.Stop:
                var pts = new PointCollection();
                for (int i = 0; i < 6; i++)
                {
                    double a = Math.PI / 180 * (60 * i - 90);
                    pts.Add(new Point(10 + 8.5 * Math.Cos(a), 10 + 8.5 * Math.Sin(a)));
                }
                Stroke(canvas, new Polygon { Points = pts, StrokeLineJoin = PenLineJoin.Round }, 1.6);
                Stroke(canvas, new Line { X1 = 7.4, Y1 = 7.4, X2 = 12.6, Y2 = 12.6 }, 1.7);
                Stroke(canvas, new Line { X1 = 12.6, Y1 = 7.4, X2 = 7.4, Y2 = 12.6 }, 1.7);
                break;
        }
        Content = new Viewbox { Width = 23, Height = 23, Child = canvas };
        Loaded += (_, _) => Sync();
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => Sync());
    }

    private void Stroke(Canvas canvas, Shape s, double w)
    {
        s.StrokeThickness = w;
        s.StrokeStartLineCap = s.StrokeEndLineCap = PenLineCap.Round;
        s.StrokeLineJoin = PenLineJoin.Round;
        canvas.Children.Add(s);
        _strokes.Add(s);
    }

    private void Sync()
    {
        foreach (var s in _strokes) s.Stroke = Foreground;
        foreach (var s in _fills) s.Fill = Foreground;
    }
}
