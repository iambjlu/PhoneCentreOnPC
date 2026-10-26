using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PhoneCentre.Ui;

/// <summary>內容放不下時，在可捲動的那一端出現小箭頭，按下去往那個方向捲動一頁（捲軸隱藏）。對應 ArrowScroll.swift。</summary>
public sealed class ArrowScroll : Grid
{
    private readonly ScrollViewer _sv;
    private readonly Button _back, _forward;
    private readonly bool _horizontal;

    public ArrowScroll(UIElement content, bool horizontal = true)
    {
        _horizontal = horizontal;
        _sv = new ScrollViewer
        {
            Content = content,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled,
            VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled,
        };
        Children.Add(_sv);
        _back = Arrow(false);
        _forward = Arrow(true);
        Children.Add(_back); Children.Add(_forward);
        _sv.ViewChanged += (_, _) => Update();
        _sv.SizeChanged += (_, _) => Update();
        _sv.LayoutUpdated += (_, _) => Update();
        Update();
    }

    /// <summary>換掉捲動區裡的內容</summary>
    public void SetContent(UIElement content) { _sv.Content = content; Update(); }

    private double Offset => _horizontal ? _sv.HorizontalOffset : _sv.VerticalOffset;
    private double Extent => _horizontal ? _sv.ExtentWidth : _sv.ExtentHeight;
    private double Visible => _horizontal ? _sv.ViewportWidth : _sv.ViewportHeight;

    private void Update()
    {
        _back.Visibility = Offset > 1 ? Visibility.Visible : Visibility.Collapsed;
        _forward.Visibility = Offset + Visible < Extent - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button Arrow(bool forward)
    {
        var icon = new MIcon(_horizontal ? (forward ? "chevron_right" : "chevron_left") : (forward ? "expand_more" : "expand_more"), 18) { Foreground = Ui.Hex(0xFFFFFFFF) };
        if (!_horizontal && !forward) icon.RenderTransform = new RotateTransform { Angle = 180, CenterX = 9, CenterY = 9 };
        var b = new Button
        {
            Content = icon, Padding = new Thickness(0), CornerRadius = new CornerRadius(15),
            Width = _horizontal ? 24 : 30, Height = _horizontal ? 30 : 24,
            Background = Ui.Hex(0x8C000000), BorderBrush = Ui.Hex(0x40FFFFFF), BorderThickness = new Thickness(0.5),
            HorizontalAlignment = _horizontal ? (forward ? HorizontalAlignment.Right : HorizontalAlignment.Left) : HorizontalAlignment.Center,
            VerticalAlignment = _horizontal ? VerticalAlignment.Center : (forward ? VerticalAlignment.Bottom : VerticalAlignment.Top),
            Margin = new Thickness(2),
        };
        ToolTipService.SetToolTip(b, forward ? "往後捲動" : "往前捲動");
        b.Click += (_, _) =>
        {
            double page = Math.Max(Visible * 0.8, 40);
            double target = forward ? Math.Min(Offset + page, Extent - Visible) : Math.Max(Offset - page, 0);
            if (_horizontal) _sv.ChangeView(target, null, null); else _sv.ChangeView(null, target, null);
        };
        return b;
    }
}
