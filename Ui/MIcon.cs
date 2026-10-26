using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace PhoneCentre.Ui;

/// <summary>Material 圖示（向量路徑，顏色跟著 Foreground）</summary>
public sealed class MIcon : UserControl
{
    private readonly Microsoft.UI.Xaml.Shapes.Path _path = new();

    public MIcon(string name, double size = 18)
    {
        IsTabStop = false;
        Width = Height = size;
        _path.Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), MaterialIconData.Paths[name]);
        var grid = new Grid { Width = 24, Height = 24 };
        grid.Children.Add(_path);
        Content = new Viewbox { Width = size, Height = size, Child = grid };
        Loaded += (_, _) => _path.Fill = Foreground;
        RegisterPropertyChangedCallback(ForegroundProperty, (_, _) => _path.Fill = Foreground);
    }
}
