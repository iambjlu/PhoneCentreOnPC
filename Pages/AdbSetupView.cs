using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneCentre.Adb;
using PhoneCentre.Services;

namespace PhoneCentre.Pages;

/// <summary>找不到 adb 時：一鍵從 Google 官方下載安裝</summary>
public sealed class AdbSetupView : UserControl
{
    public AdbSetupView()
    {
        var message = Ui.Ui.Text("", 14, true);
        var progress = new ProgressRing { IsActive = false, Visibility = Visibility.Collapsed, Width = 28, Height = 28 };
        var button = new Button { Style = (Style)Application.Current.Resources["AccentButtonStyle"], Padding = new Thickness(18, 10, 18, 10) };
        button.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new Ui.MIcon("download", 18), new TextBlock { Text = "下載並安裝 adb" } } };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false; progress.IsActive = true; progress.Visibility = Visibility.Visible;
            try
            {
                await AdbLocator.InstallAsync(m => message.Text = m);
                await DeviceManager.Shared.RefreshAsync();
            }
            catch (Exception e) { message.Text = e.Message; }
            button.IsEnabled = true; progress.IsActive = false; progress.Visibility = Visibility.Collapsed;
        };
        var body = Ui.Ui.Text("PhoneCentre 透過 adb 和手機溝通。按下按鈕後會從 Google 官方網站下載 Android SDK Platform-Tools，安裝到 PhoneCentre 自己的資料夾，不會影響系統其他設定。", 14, true);
        body.TextWrapping = TextWrapping.Wrap; body.TextAlignment = TextAlignment.Center; body.MaxWidth = 460;
        var terms = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap };
        terms.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "下載即表示同意 " });
        var link = new Microsoft.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri("https://developer.android.com/studio/terms") };
        link.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "Android SDK 授權條款" });
        terms.Inlines.Add(link);
        Content = new StackPanel
        {
            Spacing = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(40),
            Children =
            {
                new Ui.MIcon("build", 48) { Foreground = Ui.Ui.Secondary, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "需要 Android 偵錯工具（adb）", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                body, terms, button, progress, message,
                Ui.Ui.Text("已經有 adb？可在「設定 → 連線」指定路徑。", 12, true),
            },
        };
    }
}
