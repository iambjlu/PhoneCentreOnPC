using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace PhoneCentre.Ui;

/// <summary>設定視窗用的「分組表單」零件（對應 SwiftUI 的 Form.formStyle(.grouped)）：標題＋圓角卡片，卡片裡每一列以分隔線隔開</summary>
internal static class Form
{
    public static FrameworkElement Section(string? title, params FrameworkElement[] rows)
    {
        var col = new StackPanel { Spacing = 6 };
        if (title is not null) { var t = Ui.Text(title, 13, true, true); t.Margin = new Thickness(4, 0, 0, 0); col.Children.Add(t); }
        var inner = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) { var d = Ui.Divider(0.12); d.Margin = new Thickness(14, 0, 14, 0); inner.Children.Add(d); }
            rows[i].Margin = rows[i] is TextBlock ? new Thickness(14, 8, 14, 8) : new Thickness(14, 9, 14, 9);
            inner.Children.Add(rows[i]);
        }
        col.Children.Add(new Border
        {
            Child = inner, Background = Ui.Hex(0x0DFFFFFF), BorderBrush = Ui.Hex(0x1AFFFFFF), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
        });
        return col;
    }

    /// <summary>左邊標題、右邊控制項的一列</summary>
    public static Grid Row(string label, FrameworkElement control)
    {
        var g = new Grid { MinHeight = 32 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var t = Ui.Text(label, 14); t.VerticalAlignment = VerticalAlignment.Center; t.TextWrapping = TextWrapping.Wrap;
        g.Children.Add(t);
        control.VerticalAlignment = VerticalAlignment.Center; control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1); g.Children.Add(control);
        return g;
    }

    public static Grid Toggle(string label, bool value, Action<bool> set, bool enabled = true)
    {
        var sw = new ToggleSwitch { IsOn = value, OnContent = null, OffContent = null, MinWidth = 0, IsEnabled = enabled };
        sw.Toggled += (_, _) => set(sw.IsOn);
        return Row(label, sw);
    }

    public static Grid Combo<T>(string label, IReadOnlyList<T> values, Func<T, string> title, T selected, Action<T> set, bool enabled = true)
    {
        var box = new ComboBox { MinWidth = 190, IsEnabled = enabled };
        foreach (var v in values) box.Items.Add(title(v));
        box.SelectedIndex = Math.Max(0, values.ToList().FindIndex(v => EqualityComparer<T>.Default.Equals(v, selected)));
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) set(values[box.SelectedIndex]); };
        return Row(label, box);
    }

    /// <summary>說明小字</summary>
    public static TextBlock Note(string text)
    {
        var t = Ui.Text(text, 12, true); t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; return t;
    }

    public static Grid Value(string label, string value)
    {
        var t = Ui.Text(value, 13, true); t.TextWrapping = TextWrapping.Wrap; t.TextAlignment = TextAlignment.Right; t.MaxWidth = 380; t.IsTextSelectionEnabled = true;
        return Row(label, t);
    }

    public static FrameworkElement Buttons(params Button[] buttons)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in buttons) p.Children.Add(b);
        return p;
    }

    public static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text };
        b.Click += (_, _) => click();
        return b;
    }
}
