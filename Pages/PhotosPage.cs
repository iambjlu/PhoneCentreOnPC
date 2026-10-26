using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhoneCentre.Pages;

/// <summary>相機膠卷的一個格子：縮圖、影片的播放標記、選取框與勾勾。</summary>
internal sealed class PhotoCell : UserControl
{
    public RemoteFile? File { get; private set; }
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill };
    private readonly Border _play = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6) };
    private readonly Border _frame = new() { BorderThickness = new Thickness(3), BorderBrush = Ui.Ui.Accent, Visibility = Visibility.Collapsed };
    private readonly Border _check = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = Ui.Ui.Accent, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(6) };

    public PhotoCell()
    {
        IsTabStop = false; CanDrag = true;
        _play.Child = new MIcon("play_arrow", 18) { Foreground = Ui.Ui.Hex(0xFFFFFFFF) };
        _check.Child = new MIcon("check", 14) { Foreground = Ui.Ui.Hex(0xFFFFFFFF), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Content = new Grid { Background = Ui.Ui.Hex(0x1F808080), Children = { _image, _play, _frame, _check } };
        HorizontalContentAlignment = HorizontalAlignment.Stretch; VerticalContentAlignment = VerticalAlignment.Stretch;
    }

    public void Bind(RemoteFile f, BitmapImage? img, bool selected)
    {
        File = f; _image.Source = img;
        _play.Visibility = f.IsVideo ? Visibility.Visible : Visibility.Collapsed;
        SetSelected(selected);
    }

    public void SetImage(BitmapImage img) => _image.Source = img;

    public void SetSelected(bool on)
    {
        _frame.Visibility = _check.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        _play.Visibility = File is { IsVideo: true } && !on ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Clear() { File = null; _image.Source = null; SetSelected(false); }
}

/// <summary>ItemsRepeater 的元素工廠：回收格子，捲動時不用一直重建（虛擬化）</summary>
internal sealed class PhotoCellFactory : IElementFactory
{
    private readonly Stack<PhotoCell> _pool = new();
    private readonly Action<PhotoCell> _init;
    public PhotoCellFactory(Action<PhotoCell> init) => _init = init;

    public UIElement GetElement(ElementFactoryGetArgs args)
    {
        if (_pool.Count > 0) return _pool.Pop();
        var c = new PhotoCell(); _init(c); return c;
    }

    public void RecycleElement(ElementFactoryRecycleArgs args)
    {
        if (args.Element is PhotoCell c) { c.Clear(); _pool.Push(c); }
    }
}

/// <summary>相機膠卷（對應 CameraRollView.swift）：相簿、全部／照片／影片篩選、縮放滑桿、多選（Ctrl／Shift）、匯出、刪除、拖入匯入、拖出到檔案總管、雙擊檢視</summary>
public sealed class PhotosPage : UserControl
{
    /// <summary>「所有照片」：DCIM 底下所有資料夾（含子資料夾）的照片與影片</summary>
    private const string AllPhotos = "@all";

    private readonly string _deviceId;
    private readonly ThumbnailLoader _thumbs;
    private List<string> _albums = [];
    private string _album = AllPhotos;
    private List<RemoteFile> _all = [];
    private int _filter;                                    // 0 全部、1 照片、2 影片
    private readonly HashSet<string> _selection = [];
    private string? _anchor;
    private bool _loading;
    private int _loadId;
    private double _zoom = 140;
    private string _status = "";

    private readonly ComboBox _albumBox = new() { MinWidth = 160 };
    private readonly TextBlock _count = Ui.Ui.Text("", 13, true);
    private readonly StackPanel _selectionBar = new() { Orientation = Orientation.Horizontal, Spacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _selectedText = Ui.Ui.Text("", 13, true);
    private readonly ItemsRepeater _grid = new();
    private readonly UniformGridLayout _layout = new() { MinColumnSpacing = 2, MinRowSpacing = 2, ItemsStretch = UniformGridLayoutItemsStretch.Uniform };
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Ui.Tertiary };
    private readonly TextBlock _statusText = Ui.Ui.Text("", 12, true);
    private readonly Border _statusBar = new() { Padding = new Thickness(10, 5, 10, 5), Visibility = Visibility.Collapsed };
    private readonly HashSet<PhotoCell> _realized = [];
    private bool _updatingAlbum;

    private Device Device => DeviceManager.Shared.Device(_deviceId)!;
    private RemoteFS Fs => new(Device);
    private List<RemoteFile> Items => _all.Where(f => _filter == 0 || (_filter == 1 ? !f.IsVideo : f.IsVideo)).ToList();

    public PhotosPage(string deviceId)
    {
        _deviceId = deviceId;
        _thumbs = new ThumbnailLoader(Device);
        _thumbs.Loaded += path =>
        {
            foreach (var c in _realized) if (c.File?.Path == path && _thumbs.Get(c.File) is { } img) c.SetImage(img);
        };

        _albumBox.SelectionChanged += async (_, _) =>
        {
            if (_updatingAlbum || _albumBox.SelectedIndex < 0 || _albumBox.SelectedIndex >= _albums.Count) return;
            _album = _albums[_albumBox.SelectedIndex]; await LoadAsync();
        };
        var seg = new SegmentedBar(["全部", "照片", "影片"]);
        seg.SelectionChanged += i => { _filter = i; _selection.IntersectWith(Items.Select(f => f.Path)); Render(); };

        var export = new Button { Content = "匯出…" }; export.Click += (_, _) => Export();
        var del = new Button { Content = "刪除" }; del.Click += (_, _) => Delete();
        _selectionBar.Children.Add(_selectedText); _selectionBar.Children.Add(export); _selectionBar.Children.Add(del);

        var zoomOut = new MIcon("zoom_out", 18) { Foreground = Ui.Ui.Secondary };
        var zoomIn = new MIcon("zoom_in", 18) { Foreground = Ui.Ui.Secondary };
        var slider = new Slider { Minimum = 80, Maximum = 280, Value = _zoom, Width = 120, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) => { _zoom = e.NewValue; ApplyZoom(); };
        var refresh = new Button { Content = new MIcon("refresh", 16), Padding = new Thickness(8) };
        refresh.Click += async (_, _) => await LoadAsync();

        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(10), VerticalAlignment = VerticalAlignment.Center };
        bar.Children.Add(Ui.Ui.Text("相簿", 14)); bar.Children.Add(_albumBox); bar.Children.Add(seg); bar.Children.Add(_count);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        right.Children.Add(_selectionBar); right.Children.Add(zoomOut); right.Children.Add(slider); right.Children.Add(zoomIn); right.Children.Add(refresh);
        var top = new Grid { BorderBrush = Ui.Ui.Hex(0x1AFFFFFF), BorderThickness = new Thickness(0, 0, 0, 1) };
        top.Children.Add(bar); top.Children.Add(right);

        _grid.Layout = _layout;
        _grid.ItemTemplate = new PhotoCellFactory(Wire);
        _grid.ElementPrepared += (_, e) =>
        {
            if (e.Element is not PhotoCell c || e.Index < 0 || e.Index >= _shown.Count) return;
            var f = _shown[e.Index];
            c.Bind(f, _thumbs.Appear(f), _selection.Contains(f.Path));
            _realized.Add(c);
        };
        _grid.ElementClearing += (_, e) =>
        {
            if (e.Element is PhotoCell c) { if (c.File is { } f) _thumbs.Disappear(f); _realized.Remove(c); }
        };
        var scroll = new ScrollViewer { Content = new Grid { Padding = new Thickness(2), Children = { _grid } } };
        var body = new Grid { Children = { scroll, _ring, _empty } };
        body.AllowDrop = true;
        body.DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        body.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            ImportFiles((await e.DataView.GetStorageItemsAsync()).Select(i => i.Path).ToList());
        };
        _statusBar.Child = _statusText;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.Children.Add(top);
        Grid.SetRow(body, 1); root.Children.Add(body);
        Grid.SetRow(_statusBar, 2); root.Children.Add(_statusBar);
        Content = root;

        KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.A && IsDown(VirtualKey.Control)) { _selection.Clear(); foreach (var f in Items) _selection.Add(f.Path); RefreshSelection(); e.Handled = true; }
            else if (e.Key == VirtualKey.Delete) Delete();
        };
        ApplyZoom();
        _ = InitAsync();
    }

    private List<RemoteFile> _shown = [];

    private static bool IsDown(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ApplyZoom()
    {
        _layout.MinItemWidth = _layout.MinItemHeight = _zoom;
    }

    /// <summary>新格子的事件：點選、雙擊檢視、右鍵、拖出</summary>
    private void Wire(PhotoCell c)
    {
        c.Tapped += (_, _) => { if (c.File is { } f) Click(f); Focus(FocusState.Programmatic); };
        c.DoubleTapped += (_, _) => { if (c.File is { } f) View(f); };
        c.DragStarting += (_, e) =>
        {
            if (c.File is not { } f) { e.Cancel = true; return; }
            if (!_selection.Contains(f.Path)) { _selection.Clear(); _selection.Add(f.Path); RefreshSelection(); }
            RemoteDrag.Provide(e.Data, Items.Where(i => _selection.Contains(i.Path)).ToList(), Device);
        };
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string t, Action a) { var i = new MenuFlyoutItem { Text = t }; i.Click += (_, _) => a(); return i; }
        menu.Items.Add(Item("打開", () => { if (c.File is { } f) View(f); }));
        menu.Items.Add(Item("匯出…", () => { EnsureSelected(c); Export(); }));
        menu.Items.Add(Item("刪除", () => { EnsureSelected(c); Delete(); }));
        c.ContextFlyout = menu;
    }

    private void EnsureSelected(PhotoCell c)
    {
        if (c.File is { } f && !_selection.Contains(f.Path)) { _selection.Clear(); _selection.Add(f.Path); RefreshSelection(); }
    }

    /// <summary>跟檔案總管／照片一樣：Ctrl 點選加入或取消、Shift 點選選取從上一次點的到這一個之間的範圍、一般點選只選這一個</summary>
    private void Click(RemoteFile f)
    {
        var items = Items;
        bool shift = IsDown(VirtualKey.Shift), ctrl = IsDown(VirtualKey.Control);
        int i = _anchor is null ? -1 : items.FindIndex(x => x.Path == _anchor), j = items.FindIndex(x => x.Path == f.Path);
        if (shift && i >= 0 && j >= 0)
        {
            var range = items.Skip(Math.Min(i, j)).Take(Math.Abs(i - j) + 1).Select(x => x.Path);
            if (!ctrl) _selection.Clear();
            foreach (var p in range) _selection.Add(p);
            RefreshSelection(); return;
        }
        if (ctrl || shift) { if (!_selection.Remove(f.Path)) _selection.Add(f.Path); }
        else { _selection.Clear(); _selection.Add(f.Path); }
        _anchor = f.Path;
        RefreshSelection();
    }

    private void RefreshSelection()
    {
        foreach (var c in _realized) if (c.File is { } f) c.SetSelected(_selection.Contains(f.Path));
        _selectionBar.Visibility = _selection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _selectedText.Text = $"已選取 {_selection.Count} 個";
    }

    // MARK: 載入

    private static string AlbumTitle(string path) => path switch
    {
        AllPhotos => "所有照片",
        "/sdcard/DCIM/Camera" => "相機",
        "/sdcard/DCIM/Screenshots" or "/sdcard/Pictures/Screenshots" => "截圖",
        _ => path[(path.LastIndexOf('/') + 1)..],
    };

    private async Task InitAsync()
    {
        var list = new List<string> { AllPhotos, "/sdcard/DCIM/Camera" };
        try
        {
            var dcim = await Task.Run(() => Fs.ListAsync("/sdcard/DCIM", false));
            list.AddRange(dcim.Where(f => f.IsDir && f.Path != "/sdcard/DCIM/Camera").Select(f => f.Path));
        }
        catch { }
        try { list.AddRange((await Task.Run(() => Fs.ListAsync("/sdcard/Pictures", false))).Where(f => f.IsDir).Select(f => f.Path)); } catch { }
        _albums = list;
        _updatingAlbum = true;
        _albumBox.Items.Clear();
        foreach (var a in list) _albumBox.Items.Add(AlbumTitle(a));
        _albumBox.SelectedIndex = list.IndexOf(_album);
        _updatingAlbum = false;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        _loading = true; _selection.Clear(); _anchor = null; Render();
        List<RemoteFile> list = [];
        try
        {
            var fs = Fs; var album = _album;
            list = await Task.Run(() => album == AllPhotos ? fs.ListRecursiveAsync("/sdcard/DCIM") : fs.ListAsync(album, false));
        }
        catch { }
        if (id != _loadId) return;
        _all = list.Where(f => f.IsMedia).OrderByDescending(f => f.Modified).ToList();
        _loading = false;
        Render();
    }

    private void Render()
    {
        _shown = Items;
        _grid.ItemsSource = _shown;
        _ring.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = !_loading && _shown.Count == 0 ? (_filter == 0 ? "這個相簿沒有照片或影片" : $"這個相簿沒有{(_filter == 1 ? "照片" : "影片")}") : "";
        _count.Text = $"{_shown.Count} 個項目";
        RefreshSelection();
        _statusBar.Visibility = _status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _statusText.Text = _status;
    }

    // MARK: 動作

    private void View(RemoteFile f)
    {
        var w = new MediaViewerWindow(Device, Items, f);
        w.Activate();
    }

    private async void Export()
    {
        var sel = Items.Where(f => _selection.Contains(f.Path)).ToList();
        if (sel.Count == 0) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        var used = new HashSet<string>();
        for (int i = 0; i < sel.Count; i++)
        {
            SetStatus($"匯出中 ({i + 1}/{sel.Count})：{sel[i].Name}");
            // 「所有照片」可能有不同資料夾的同名檔案：名稱重複就加上編號，不互相覆蓋
            try { await Device.Client.PullAsync(sel[i].Path, UniquePath(folder.Path, sel[i].Name, used)); } catch { }
        }
        SetStatus($"已匯出 {sel.Count} 個項目到 {folder.Name}");
    }

    private static string UniquePath(string dir, string name, HashSet<string> used)
    {
        var b = System.IO.Path.GetFileNameWithoutExtension(name); var ext = System.IO.Path.GetExtension(name);
        var cand = name; int n = 2;
        while (used.Contains(cand) || System.IO.File.Exists(System.IO.Path.Combine(dir, cand))) cand = $"{b} {n++}{ext}";
        used.Add(cand);
        return System.IO.Path.Combine(dir, cand);
    }

    private async void Delete()
    {
        var sel = Items.Where(f => _selection.Contains(f.Path)).ToList();
        if (sel.Count == 0) return;
        var d = new ContentDialog
        {
            Title = $"要從手機刪除 {sel.Count} 個項目嗎？", Content = "會直接從手機刪除，無法復原。",
            PrimaryButtonText = "刪除", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Close,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try { await Fs.DeleteAsync(sel.Select(f => f.Path)); } catch (Exception e) { SetStatus(e.Message); }
        await LoadAsync();
    }

    private async void ImportFiles(List<string> paths)
    {
        // 「所有照片」不是實際的資料夾，匯入到相機資料夾
        var target = _album == AllPhotos ? "/sdcard/DCIM/Camera" : _album;
        for (int i = 0; i < paths.Count; i++)
        {
            var name = System.IO.Path.GetFileName(paths[i].TrimEnd('\\', '/'));
            SetStatus($"匯入中 ({i + 1}/{paths.Count})：{name}");
            try { await Device.Client.PushAsync(paths[i], target + "/" + name); } catch { }
        }
        SetStatus($"已匯入 {paths.Count} 個項目");
        await LoadAsync();
    }

    private void SetStatus(string s) { _status = s; _statusText.Text = s; _statusBar.Visibility = s.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
}
