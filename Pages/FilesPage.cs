using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Support;
using PhoneCentre.Ui;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace PhoneCentre.Pages;

/// <summary>檔案總管風格的手機檔案管理（對應 FileBrowserView.swift）：位置側邊欄、導覽列與路徑列、可排序表格、拖入上傳、拖出下載</summary>
public sealed class FilesPage : UserControl
{
    private static readonly (string Title, string Icon, string Path)[] Places =
    [
        ("內部儲存空間", "storage", "/sdcard"), ("下載項目", "download", "/sdcard/Download"), ("相機", "photo_camera", "/sdcard/DCIM"),
        ("圖片", "image", "/sdcard/Pictures"), ("文件", "description", "/sdcard/Documents"), ("音樂", "music_note", "/sdcard/Music"),
        ("影片", "movie", "/sdcard/Movies"), ("暫存 (/data/local/tmp)", "build", "/data/local/tmp"), ("根目錄", "storage", "/"),
    ];

    private readonly string _deviceId;
    private string _path = "/sdcard";
    private readonly List<string> _back = [], _forward = [];
    private List<RemoteFile> _files = [];
    private bool _loading, _showHidden;
    private string? _error;
    private string _status = "";
    private string _sortKey = "name";
    private bool _sortAsc = true;
    private int _loadId;

    private readonly StackPanel _placesList = new() { Spacing = 2, Padding = new Thickness(8, 4, 8, 8) };
    private readonly StackPanel _pathBar = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly Grid _header = new();
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, CanDragItems = true, IsItemClickEnabled = false };
    private readonly TextBlock _statusText = Ui.Ui.Text("", 12, true);
    private readonly Grid _overlayHost = new();
    private readonly ProgressRing _ring = new() { IsActive = true, Width = 28, Height = 28, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _overlayText = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Ui.Secondary, Margin = new Thickness(20) };
    private Button _backBtn = null!, _fwdBtn = null!, _upBtn = null!, _dlBtn = null!;

    private Device Device => DeviceManager.Shared.Device(_deviceId)!;
    private RemoteFS Fs => new(Device);

    public FilesPage(string deviceId)
    {
        _deviceId = deviceId;
        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = new StackPanel { Background = Ui.Ui.Hex(0x0DFFFFFF) };
        var sideHead = Ui.Ui.Text("位置", 12, true, true); sideHead.Margin = new Thickness(18, 12, 0, 4);
        side.Children.Add(sideHead); side.Children.Add(_placesList);
        root.Children.Add(side);

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(main, 1); root.Children.Add(main);

        main.Children.Add(BuildToolbar());
        BuildHeader();
        Grid.SetRow(_header, 1); main.Children.Add(_header);

        _overlayHost.Children.Add(_list); _overlayHost.Children.Add(_ring); _overlayHost.Children.Add(_overlayText);
        Grid.SetRow(_overlayHost, 2); main.Children.Add(_overlayHost);
        var foot = new Border { Padding = new Thickness(10, 4, 10, 4), BorderBrush = Ui.Ui.Hex(0x1AFFFFFF), BorderThickness = new Thickness(0, 1, 0, 0), Child = _statusText };
        Grid.SetRow(foot, 3); main.Children.Add(foot);
        Content = root;

        // 緊湊的列：預設的 ListViewItem 最小高度 40、左右留白太大，跟標題列對不齊
        var rowStyle = new Style(typeof(ListViewItem));
        rowStyle.Setters.Add(new Setter(MinHeightProperty, 30.0));
        rowStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(14, 0, 14, 0)));
        _list.ItemContainerStyle = rowStyle;
        _list.SelectionChanged += (_, _) => UpdateButtons();
        _list.DoubleTapped += (_, e) => { if (RowOf(e.OriginalSource) is { } f) Open(f); };
        _list.KeyDown += (_, e) => { if (e.Key == VirtualKey.Delete) DeleteSelected(); else if (e.Key == VirtualKey.Enter && Selected().FirstOrDefault() is { } f) Open(f); };
        _list.DragItemsStarting += OnDragStarting;
        _list.ContextRequested += OnContext;
        // 拖入上傳
        AllowDrop = true;
        DragOver += (_, e) => { if (e.DataView.Contains(StandardDataFormats.StorageItems)) e.AcceptedOperation = DataPackageOperation.Copy; };
        Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            Push(items.Select(i => i.Path).ToList());
        };

        RebuildPlaces();
        _ = LoadAsync();
    }

    // MARK: 版面

    private FrameworkElement BuildToolbar()
    {
        Button Tool(string icon, string tip, Action click)
        {
            var b = new Button { Content = new MIcon(icon, 18), Padding = new Thickness(7), Background = null, BorderThickness = new Thickness(0) };
            ToolTipService.SetToolTip(b, tip); b.Click += (_, _) => click(); return b;
        }
        _backBtn = Tool("chevron_left", "上一頁", GoBack);
        _fwdBtn = Tool("chevron_right", "下一頁", GoForward);
        _upBtn = Tool("arrow_upward", "上一層", GoUp);
        var hidden = new ToggleButton { Content = new MIcon("visibility", 18), Padding = new Thickness(7), Background = null, BorderThickness = new Thickness(0) };
        ToolTipService.SetToolTip(hidden, "顯示隱藏檔");
        hidden.Click += async (_, _) => { _showHidden = hidden.IsChecked == true; await LoadAsync(); };
        _dlBtn = Tool("download", "下載到這台電腦", DownloadSelected);

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { _backBtn, _fwdBtn, _upBtn } };
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2,
            Children = { hidden, Tool("create_new_folder", "新增資料夾", NewFolder), Tool("upload", "上傳檔案到這裡", Upload), _dlBtn, Tool("refresh", "重新整理", () => _ = LoadAsync()) },
        };
        var path = new ScrollViewer { Content = _pathBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalAlignment = VerticalAlignment.Center };
        var bar = new Grid { Padding = new Thickness(8), ColumnSpacing = 6 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(left);
        Grid.SetColumn(path, 1); bar.Children.Add(path);
        Grid.SetColumn(right, 2); bar.Children.Add(right);
        return bar;
    }

    private static void Columns(Grid g)
    {
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 200 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
    }

    private void BuildHeader()
    {
        _header.Children.Clear(); _header.ColumnDefinitions.Clear();
        Columns(_header);
        _header.Padding = new Thickness(14, 6, 14, 6);
        _header.BorderBrush = Ui.Ui.Hex(0x1AFFFFFF); _header.BorderThickness = new Thickness(0, 1, 0, 1);
        (string Key, string Title)[] cols = [("name", "名稱"), ("date", "修改日期"), ("size", "大小"), ("kind", "種類")];
        for (int i = 0; i < cols.Length; i++)
        {
            var (key, title) = cols[i];
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Background = Ui.Ui.Hex(0x01000000) };
            row.Children.Add(Ui.Ui.Text(title, 12, false, true));
            if (_sortKey == key) row.Children.Add(new MIcon("arrow_upward", 12) { RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = _sortAsc ? null : new Microsoft.UI.Xaml.Media.RotateTransform { Angle = 180 } });
            row.Tapped += (_, _) => { if (_sortKey == key) _sortAsc = !_sortAsc; else { _sortKey = key; _sortAsc = true; } BuildHeader(); RenderRows(); };
            Grid.SetColumn(row, i); _header.Children.Add(row);
        }
    }

    private void RebuildPlaces()
    {
        _placesList.Children.Clear();
        foreach (var (title, icon, p) in Places)
        {
            bool on = _path == p;
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(8), Background = on ? Ui.Ui.Hex(0x330A84FF) : Ui.Ui.Hex(0x01000000) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new MIcon(icon, 18) { Foreground = Ui.Ui.Accent });
            var t = Ui.Ui.Text(title, 14); Grid.SetColumn(t, 1); row.Children.Add(t);
            row.Tapped += (_, _) => Go(p);
            _placesList.Children.Add(row);
        }
    }

    private void RebuildPathBar()
    {
        _pathBar.Children.Clear();
        Button Crumb(string text, string target)
        {
            var b = new Button { Content = text, Padding = new Thickness(6, 3, 6, 3), Background = null, BorderThickness = new Thickness(0) };
            b.Click += (_, _) => Go(target); return b;
        }
        _pathBar.Children.Add(Crumb("/", "/"));
        var comps = _path == "/" ? [] : _path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < comps.Length; i++)
        {
            _pathBar.Children.Add(new MIcon("chevron_right", 14) { Foreground = Ui.Ui.Tertiary, VerticalAlignment = VerticalAlignment.Center });
            _pathBar.Children.Add(Crumb(comps[i], "/" + string.Join("/", comps.Take(i + 1))));
        }
    }

    // MARK: 表格

    private IEnumerable<RemoteFile> Sorted()
    {
        Func<RemoteFile, object> key = _sortKey switch
        {
            "date" => f => f.Modified, "size" => f => f.Size, "kind" => f => f.Kind, _ => f => f.Name,
        };
        IComparer<object> cmp = _sortKey == "name" || _sortKey == "kind"
            ? Comparer<object>.Create((a, b) => AppIndex.NameComparer.Compare((string)a, (string)b))
            : Comparer<object>.Create((a, b) => Comparer<IComparable>.Default.Compare((IComparable)a, (IComparable)b));
        var s = (_sortAsc ? _files.OrderBy(key, cmp) : _files.OrderByDescending(key, cmp)).ToList();
        return s.Where(f => f.IsDir).Concat(s.Where(f => !f.IsDir));   // 資料夾一律在前面，像檔案總管
    }

    private void RenderRows()
    {
        _list.Items.Clear();
        foreach (var f in Sorted())
        {
            var g = new Grid { Padding = new Thickness(0, 3, 0, 3), Tag = f };
            Columns(g);
            var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            name.Children.Add(new MIcon(f.IconName, 20) { Foreground = f.IsDir ? Ui.Ui.Hex(0xFF8FB4D9) : Ui.Ui.Secondary });
            name.Children.Add(Ui.Ui.Text(f.Name, 14));
            g.Children.Add(name);
            void Cell(int col, string text) { var t = Ui.Ui.Text(text, 13, true); Grid.SetColumn(t, col); g.Children.Add(t); }
            Cell(1, f.Modified.ToLocalTime().ToString("yyyy年M月d日 HH:mm"));
            Cell(2, f.SizeString);
            Cell(3, f.Kind);
            _list.Items.Add(g);
        }
        _ring.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
        _overlayText.Text = _loading ? "" : _error ?? (_files.Count == 0 ? "資料夾是空的" : "");
        _statusText.Text = _status.Length == 0 ? $"{_files.Count} 個項目" : _status;
        UpdateButtons();
    }

    private IEnumerable<RemoteFile> Selected() => _list.SelectedItems.OfType<FrameworkElement>().Select(e => e.Tag).OfType<RemoteFile>();
    private static RemoteFile? RowOf(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d is FrameworkElement { Tag: RemoteFile f }) return f;
        return null;
    }

    private void UpdateButtons()
    {
        _backBtn.IsEnabled = _back.Count > 0; _fwdBtn.IsEnabled = _forward.Count > 0;
        _upBtn.IsEnabled = _path != "/"; _dlBtn.IsEnabled = _list.SelectedItems.Count > 0;
    }

    // MARK: 導覽

    private void Go(string p)
    {
        if (p == _path) return;
        _back.Add(_path); _forward.Clear(); _path = p;
        _ = LoadAsync();
    }

    private void GoBack() { if (_back.Count == 0) return; _forward.Add(_path); _path = _back[^1]; _back.RemoveAt(_back.Count - 1); _ = LoadAsync(); }
    private void GoForward() { if (_forward.Count == 0) return; _back.Add(_path); _path = _forward[^1]; _forward.RemoveAt(_forward.Count - 1); _ = LoadAsync(); }
    private void GoUp()
    {
        var i = _path.TrimEnd('/').LastIndexOf('/');
        Go(i <= 0 ? "/" : _path[..i]);
    }

    private async Task LoadAsync()
    {
        int id = ++_loadId;
        _loading = true; _error = null; RebuildPlaces(); RebuildPathBar(); RenderRows();
        List<RemoteFile> files = []; string? err = null;
        try { files = await Task.Run(() => Fs.ListAsync(_path, _showHidden)); }
        catch (Exception e) { err = e.Message; }
        if (id != _loadId) return;          // 已經切到別的資料夾了
        _files = files; _error = err; _loading = false;
        RenderRows();
    }

    private async void Open(RemoteFile f)
    {
        if (f.IsDir) { Go(f.Path); return; }
        try
        {
            SetStatus($"正在下載 {f.Name}…");
            var local = await Task.Run(() => Fs.DownloadToCacheAsync(f));
            await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(local));
            SetStatus("");
        }
        catch (Exception e) { SetStatus(e.Message); }
    }

    private void SetStatus(string s) { _status = s; _statusText.Text = s.Length == 0 ? $"{_files.Count} 個項目" : s; }

    // MARK: 右鍵選單與檔案操作

    private void OnContext(UIElement sender, ContextRequestedEventArgs e)
    {
        var f = RowOf(e.OriginalSource);
        if (f is not null && !_list.SelectedItems.OfType<FrameworkElement>().Any(x => ReferenceEquals(x.Tag, f)))
        {
            _list.SelectedItems.Clear();
            foreach (var item in _list.Items.OfType<FrameworkElement>().Where(x => ReferenceEquals(x.Tag, f))) _list.SelectedItems.Add(item);
        }
        var sel = Selected().ToList();
        var m = new MenuFlyout();
        MenuFlyoutItem Item(string text, Action act) { var i = new MenuFlyoutItem { Text = text }; i.Click += (_, _) => act(); return i; }
        if (sel.Count == 1)
        {
            var one = sel[0];
            m.Items.Add(Item("打開", () => Open(one)));
            m.Items.Add(Item("重新命名…", () => Rename(one)));
            m.Items.Add(Item("拷貝路徑", () => { var p = new DataPackage(); p.SetText(one.Path); Clipboard.SetContent(p); }));
        }
        if (sel.Count > 0)
        {
            m.Items.Add(Item("下載到這台電腦…", DownloadSelected));
            m.Items.Add(new MenuFlyoutSeparator());
            m.Items.Add(Item("刪除", DeleteSelected));
        }
        else
        {
            m.Items.Add(Item("新增資料夾", NewFolder));
            m.Items.Add(Item("上傳檔案到這裡…", Upload));
        }
        e.Handled = true;
        if (e.TryGetPosition(sender, out var pt)) m.ShowAt(sender, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pt });
    }

    private string Join(string name) => _path == "/" ? "/" + name : _path + "/" + name;

    private async void NewFolder()
    {
        var name = "未命名資料夾"; int i = 2;
        while (_files.Any(f => f.Name == name)) name = $"未命名資料夾 {i++}";
        try { await Fs.MkdirAsync(Join(name)); } catch (Exception e) { SetStatus(e.Message); }
        await LoadAsync();
    }

    private async void Rename(RemoteFile f)
    {
        var box = new TextBox { Text = f.Name, Width = 320 };
        var d = new ContentDialog { Title = "重新命名", Content = box, PrimaryButtonText = "重新命名", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Primary };
        if (await d.ShowAsync() != ContentDialogResult.Primary || box.Text.Trim().Length == 0 || box.Text == f.Name) return;
        try { await Fs.RenameAsync(f.Path, Join(box.Text.Trim())); } catch (Exception e) { SetStatus(e.Message); }
        await LoadAsync();
    }

    private async void DeleteSelected()
    {
        var sel = Selected().ToList();
        if (sel.Count == 0) return;
        var d = new ContentDialog
        {
            Title = sel.Count == 1 ? $"要刪除「{sel[0].Name}」嗎？" : $"要刪除 {sel.Count} 個項目嗎？",
            Content = "手機上的檔案會直接刪除，無法復原。", PrimaryButtonText = "刪除", CloseButtonText = "取消", XamlRoot = XamlRoot, DefaultButton = ContentDialogButton.Close,
        };
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;
        try { await Fs.DeleteAsync(sel.Select(f => f.Path)); } catch (Exception e) { SetStatus(e.Message); }
        await LoadAsync();
    }

    private async void Upload()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (files is { Count: > 0 }) Push(files.Select(f => f.Path).ToList());
    }

    private async void Push(List<string> paths)
    {
        for (int i = 0; i < paths.Count; i++)
        {
            var name = System.IO.Path.GetFileName(paths[i].TrimEnd('\\', '/'));
            SetStatus($"上傳中 ({i + 1}/{paths.Count})：{name}");
            try { await Device.Client.PushAsync(paths[i], Join(name)); } catch (Exception e) { SetStatus(e.Message); }
        }
        SetStatus("");
        await LoadAsync();
    }

    private async void DownloadSelected()
    {
        var sel = Selected().ToList();
        if (sel.Count == 0) return;
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        for (int i = 0; i < sel.Count; i++)
        {
            SetStatus($"下載中 ({i + 1}/{sel.Count})：{sel[i].Name}");
            try { await Device.Client.PullAsync(sel[i].Path, System.IO.Path.Combine(folder.Path, sel[i].Name)); } catch (Exception e) { SetStatus(e.Message); return; }
        }
        SetStatus($"已下載到 {folder.Path}");
    }

    /// <summary>拖出到檔案總管：放開時才從手機下載（延遲提供檔案）</summary>
    private void OnDragStarting(object sender, DragItemsStartingEventArgs e)
    {
        var items = e.Items.OfType<FrameworkElement>().Select(x => x.Tag).OfType<RemoteFile>().ToList();
        if (items.Count == 0) { e.Cancel = true; return; }
        RemoteDrag.Provide(e.Data, items, Device);
    }
}
