using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Services;
using Windows.UI;

namespace PhoneCentre.Ui;

/// <summary>共用的小零件與顏色（全部是深色主題）</summary>
internal static class Ui
{
    public static SolidColorBrush Hex(uint argb) =>
        new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    public static readonly SolidColorBrush Secondary = Hex(0xAAFFFFFF);
    public static readonly SolidColorBrush Tertiary = Hex(0x66FFFFFF);
    public static readonly SolidColorBrush Accent = Hex(0xFF0A84FF);

    public static TextBlock Text(string text, double size = 14, bool secondary = false, bool bold = false)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        if (secondary) t.Foreground = Secondary;   // 不能把 Foreground 設成 null：那會讓文字不上色而隱形
        return t;
    }

    /// <summary>毛玻璃底上疊的圓角面板（Mac 版的 liquidPanel；Windows 沒有液態玻璃，用半透明灰底）</summary>
    public static Border Panel(UIElement child, double radius = 22) => new()
    {
        Background = Hex(0x1FFFFFFF), BorderBrush = Hex(0x26FFFFFF), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius), Child = child,
    };

    /// <summary>App 圖示；還沒載入時先畫灰色圓角方塊</summary>
    public static FrameworkElement AppIcon(AppCatalog catalog, string pkg, double size)
    {
        var src = catalog.Icon(pkg);
        if (src is not null) return new Image { Source = src, Width = size, Height = size };
        return new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size * 0.22), Background = Hex(0x33FFFFFF) };
    }

    /// <summary>執行中的小圓點；目前在最上層的那個拉長成短線（啟動台、總覽、選單列同樣規則）</summary>
    public static FrameworkElement RunningMark(bool running, bool current) => new Border
    {
        Width = current ? 14 : 4, Height = 4, CornerRadius = new CornerRadius(2),
        Background = running ? Hex(0xCCFFFFFF) : null, HorizontalAlignment = HorizontalAlignment.Center,
    };

    public static Button NavButton(UIElement icon, string tip, Action click, bool enabled = true)
    {
        var b = new Button { Content = icon, Style = (Style)Application.Current.Resources["NavButtonStyle"], IsEnabled = enabled };
        ToolTipService.SetToolTip(b, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, tip);   // 無障礙名稱（螢幕閱讀器與自動化測試用）
        b.Click += (_, _) => click();
        return b;
    }

    public static Rectangle Divider(double opacity = 0.3, double w = double.NaN) => new()
    {
        Height = 1, Fill = Hex((uint)(opacity * 255) << 24 | 0xFFFFFF), Margin = new Thickness(10, 2, 10, 2),
    };
}
