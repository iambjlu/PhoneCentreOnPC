using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhoneCentre.Ui;

/// <summary>分頁列：整條膠囊，選中的項目有較亮的膠囊底（對應 Mac 版工具列的 Picker.segmented）</summary>
public sealed class SegmentedBar : UserControl
{
    private readonly List<Border> _items = [];
    private int _selected;
    public event Action<int>? SelectionChanged;

    public SegmentedBar(IEnumerable<string> titles)
    {
        IsTabStop = false;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        int i = 0;
        foreach (var t in titles)
        {
            int index = i++;
            var item = new Border
            {
                CornerRadius = new CornerRadius(15), Padding = new Thickness(16, 6, 16, 6), Background = Ui.Hex(0x01000000),
                Child = new TextBlock { Text = t, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center },
            };
            item.Tapped += (_, _) => { if (Selected != index) { Selected = index; SelectionChanged?.Invoke(index); } };
            item.PointerEntered += (_, _) => { if (index != _selected) item.Background = Ui.Hex(0x14FFFFFF); };
            item.PointerExited += (_, _) => { if (index != _selected) item.Background = Ui.Hex(0x01000000); };
            _items.Add(item); row.Children.Add(item);
        }
        Content = new Border
        {
            Background = Ui.Hex(0x33000000), BorderBrush = Ui.Hex(0x26FFFFFF), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18), Padding = new Thickness(3), Child = row,
        };
        Apply();
    }

    public int Selected
    {
        get => _selected;
        set { _selected = value; Apply(); }
    }

    private void Apply()
    {
        for (int i = 0; i < _items.Count; i++)
            _items[i].Background = i == _selected ? Ui.Hex(0x40FFFFFF) : Ui.Hex(0x01000000);
    }
}
