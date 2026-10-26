using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PhoneCentre;

/// <summary>字型測試（--fonttest）：並排顯示各字型的實際渲染，確認內嵌字型有被 XAML 用到</summary>
public sealed class FontTestWindow : Window
{
    public FontTestWindow()
    {
        Title = "字型測試";
        PhoneCentre.Support.WindowIcon.Apply(this);
        SystemBackdrop = new MicaBackdrop();
        var panel = new StackPanel { Padding = new Thickness(24), Spacing = 14 };
        foreach (var (name, family) in new[]
        {
            ("系統預設", "Segoe UI"), ("Inter", "Inter"), ("台北黑體", "Taipei Sans TC Beta"),
            ("Inter＋台北黑體", "Inter, Taipei Sans TC Beta"), ("微軟正黑體", "Microsoft JhengHei UI"),
        })
        {
            panel.Children.Add(new TextBlock { Text = name, FontSize = 12, Opacity = 0.6, FontFamily = new FontFamily("Segoe UI") });
            panel.Children.Add(new TextBlock { Text = "Galaxy S10 0123456789 Rag fi Il1 通話記錄 簡訊 相機膠卷 啟動台 總覽", FontSize = 22, FontFamily = new FontFamily(family) });
            panel.Children.Add(new TextBlock { Text = "Galaxy S10 0123456789 Rag fi Il1 通話記錄 簡訊 相機膠卷 啟動台 總覽", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, FontFamily = new FontFamily(family) });
        }
        Content = new ScrollViewer { Content = panel };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1050, 720));
    }
}
