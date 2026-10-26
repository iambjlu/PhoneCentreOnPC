using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PhoneCentre.Services;

namespace PhoneCentre.Ui;

/// <summary>App 圖示格：圖示、名稱、執行中標記。圖示載好與執行狀態改變時用 <see cref="Refresh"/> 原地更新，不用整個重建（捲動位置才不會跑掉）</summary>
public sealed class AppCell : StackPanel
{
    public string Package { get; }
    private readonly AppCatalog _catalog;
    private readonly Image _image;
    private readonly Border _placeholder;
    private readonly Border _mark = new() { Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center };

    public AppCell(AppCatalog catalog, string package, string label, double iconSize, double width)
    {
        _catalog = catalog;
        Package = package;
        Spacing = 5; Width = width; Background = Ui.Hex(0x01000000);
        _image = new Image { Width = iconSize, Height = iconSize, Visibility = Visibility.Collapsed };
        _placeholder = new Border { Width = iconSize, Height = iconSize, CornerRadius = new CornerRadius(iconSize * 0.22), Background = Ui.Hex(0x33FFFFFF) };
        var holder = new Grid { Width = iconSize, Height = iconSize, HorizontalAlignment = HorizontalAlignment.Center, Children = { _placeholder, _image } };
        var text = Ui.Text(label, 12);
        text.TextAlignment = TextAlignment.Center; text.HorizontalAlignment = HorizontalAlignment.Center; text.MaxWidth = width;
        Children.Add(holder); Children.Add(text); Children.Add(_mark);
        ToolTipService.SetToolTip(this, package);
        Refresh();
    }

    public void Refresh()
    {
        if (_image.Visibility == Visibility.Collapsed && _catalog.Icon(Package) is { } src)
        {
            _image.Source = src; _image.Visibility = Visibility.Visible; _placeholder.Visibility = Visibility.Collapsed;
        }
        bool running = _catalog.Running.Contains(Package);
        _mark.Width = Package == _catalog.Foreground ? 14 : 4;
        _mark.Background = running ? Ui.Hex(0xCCFFFFFF) : null;
    }
}
