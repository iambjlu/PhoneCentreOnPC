using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Ui;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace PhoneCentre.Pages;

/// <summary>單張檢視：照片顯示原圖、影片用播放器；左右鍵切換（對應 MediaViewer）</summary>
public sealed class MediaViewerWindow : Window
{
    private readonly Device _device;
    private readonly List<RemoteFile> _items;
    private RemoteFile _current;
    private readonly TextBlock _title = Ui.Ui.Text("", 15, false, true), _date = Ui.Ui.Text("", 13, true);
    private readonly Image _image = new() { Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform, Visibility = Visibility.Collapsed };
    private readonly MediaPlayerElement _video = new() { AreTransportControlsEnabled = true, Visibility = Visibility.Collapsed, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
    private readonly TextBlock _message = new() { Foreground = Ui.Ui.Hex(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private string? _localPath;
    private int _loadId;

    public MediaViewerWindow(Device device, List<RemoteFile> items, RemoteFile current)
    {
        _device = device; _items = items.Where(i => !i.IsDir).ToList(); _current = current;
        Title = current.Name;
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1000, 760));

        Button Nav(string icon, int delta) { var b = new Button { Content = new MIcon(icon, 18), Padding = new Thickness(8) }; b.Click += (_, _) => Step(delta); return b; }
        var open = new Button { Content = "用預設程式打開" };
        open.Click += async (_, _) => { if (_localPath is not null) await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(_localPath)); };
        var close = new Button { Content = "關閉" }; close.Click += (_, _) => Close();

        var bar = new Grid { Padding = new Thickness(10), ColumnSpacing = 10 };
        for (int i = 0; i < 4; i++) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var nav = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { Nav("chevron_left", -1), Nav("chevron_right", 1) } };
        var info = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, Children = { _title, _date } };
        bar.Children.Add(nav);
        Grid.SetColumn(info, 1); bar.Children.Add(info);
        Grid.SetColumn(open, 3); bar.Children.Add(open);
        var closeHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, close } };
        bar.Children.Remove(open);
        Grid.SetColumn(closeHost, 3); bar.Children.Add(closeHost);

        var stage = new Grid { Background = Ui.Ui.Hex(0xFF000000), Children = { _image, _video, _message } };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(bar);
        Grid.SetRow(stage, 1); root.Children.Add(stage);
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Left) Step(-1); else if (e.Key == VirtualKey.Right) Step(1); else if (e.Key == VirtualKey.Escape) Close();
        };
        Content = root;
        Closed += (_, _) => StopVideo();
        _ = LoadAsync();
    }

    private void Step(int d)
    {
        int n = _items.FindIndex(i => i.Path == _current.Path) + d;
        if (n >= 0 && n < _items.Count) { _current = _items[n]; _ = LoadAsync(); }
    }

    private void StopVideo()
    {
        if (_video.MediaPlayer is { } p) { p.Pause(); p.Dispose(); _video.SetMediaPlayer(null); }
    }

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        StopVideo();
        _image.Visibility = _video.Visibility = Visibility.Collapsed;
        _message.Text = "下載中…";
        _title.Text = _current.Name; Title = _current.Name;
        _date.Text = _current.Modified.ToLocalTime().ToString("yyyy年M月d日 HH:mm");
        try
        {
            var local = await Task.Run(() => new RemoteFS(_device).DownloadToCacheAsync(_current));
            if (id != _loadId) return;
            _localPath = local; _message.Text = "";
            if (_current.IsVideo)
            {
                var player = new MediaPlayer { Source = MediaSource.CreateFromStorageFile(await StorageFile.GetFileFromPathAsync(local)), AutoPlay = true };
                _video.SetMediaPlayer(player);
                _video.Visibility = Visibility.Visible;
            }
            else
            {
                _image.Source = new BitmapImage(new Uri(local));
                _image.Visibility = Visibility.Visible;
            }
        }
        catch (Exception e) { if (id == _loadId) _message.Text = e.Message; }
    }
}
