using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Shapes;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Ui;

namespace PhoneCentre.Mirror;

/// <summary>鏡像視窗右側欄的彈窗：畫質、聲音、桌面模式的小型啟動台、解鎖密碼對話框（對應 VideoSettingsPopover、SoundPopover、MiniLauncher、LockPasswordSheet）</summary>
internal static class Popovers
{
    private static Flyout MakeFlyout(Func<FrameworkElement> build)
    {
        var f = new Flyout { Placement = FlyoutPlacementMode.LeftEdgeAlignedTop };
        f.Opening += (_, _) => f.Content = build();
        return f;
    }

    private static Grid LabeledRow(string title, FrameworkElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Ui.Text(title, 14));
        Grid.SetColumn(control, 1); control.VerticalAlignment = VerticalAlignment.Center; g.Children.Add(control);
        return g;
    }

    // MARK: 畫質

    private static readonly (string Name, VideoOptions Options)[] Presets =
    [
        ("省流量", new VideoOptions { MaxFps = 30, BitRateMbps = 2, MaxSize = 1024 }),
        ("一般", new VideoOptions { MaxFps = 60, BitRateMbps = 8, MaxSize = 1920 }),
        ("高畫質", new VideoOptions { MaxFps = 60, BitRateMbps = 20, MaxSize = 0 }),
    ];

    public static Flyout VideoSettings(MirrorModel model, Action? closed = null)
    {
        Flyout? flyout = null;
        flyout = MakeFlyout(() =>
        {
            var o = new VideoOptions { MaxFps = model.VideoOptions.MaxFps, BitRateMbps = model.VideoOptions.BitRateMbps, MaxSize = model.VideoOptions.MaxSize };
            int PresetIndex() => Array.FindIndex(Presets, p => p.Options.MaxFps == o.MaxFps && p.Options.BitRateMbps == o.BitRateMbps && p.Options.MaxSize == o.MaxSize);

            var fps = new ComboBox { MinWidth = 120 };
            int[] fpsOpts = [15, 24, 30, 45, 60, 90, 120];
            foreach (var f in fpsOpts) fps.Items.Add($"{f} fps");
            int[] sizeOpts = [0, 2560, 1920, 1600, 1280, 1024, 800, 640];
            var size = new ComboBox { MinWidth = 120 };
            foreach (var s in sizeOpts) size.Items.Add(s == 0 ? "不限制" : $"{s} px");
            var bitrate = new Slider { Minimum = 0.5, Maximum = 40, StepFrequency = 0.1, Value = o.BitRateMbps };
            var bitrateText = Ui.Ui.Text($"{o.BitRateMbps:0.0} Mbps", 13, true);
            var seg = new SegmentedBar(["省流量", "一般", "高畫質", "自訂"]);
            bool loading = true;
            void Sync()
            {
                loading = true;
                fps.SelectedIndex = Array.IndexOf(fpsOpts, o.MaxFps);
                size.SelectedIndex = Array.IndexOf(sizeOpts, o.MaxSize);
                bitrate.Value = o.BitRateMbps; bitrateText.Text = $"{o.BitRateMbps:0.0} Mbps";
                int pi = PresetIndex(); seg.Selected = pi >= 0 ? pi : 3;
                loading = false;
            }
            seg.SelectionChanged += i =>
            {
                if (loading || i >= Presets.Length) return;
                o.MaxFps = Presets[i].Options.MaxFps; o.BitRateMbps = Presets[i].Options.BitRateMbps; o.MaxSize = Presets[i].Options.MaxSize; Sync();
            };
            fps.SelectionChanged += (_, _) => { if (!loading && fps.SelectedIndex >= 0) { o.MaxFps = fpsOpts[fps.SelectedIndex]; Sync(); } };
            size.SelectionChanged += (_, _) => { if (!loading && size.SelectedIndex >= 0) { o.MaxSize = sizeOpts[size.SelectedIndex]; Sync(); } };
            bitrate.ValueChanged += (_, e) => { if (!loading) { o.BitRateMbps = Math.Round(e.NewValue, 1); Sync(); } };
            Sync();

            var apply = new Button { Content = "套用", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };
            apply.Click += (_, _) => { flyout!.Hide(); model.ApplyVideoOptions(o); };
            var rateRow = new StackPanel { Spacing = 4, Children = { LabeledRow("位元率", bitrateText), bitrate } };
            return new StackPanel
            {
                Width = 290, Spacing = 14,
                Children = { Ui.Ui.Text("畫質", 16, false, true), seg, LabeledRow("影格率", fps), LabeledRow("解析度上限", size), rateRow, apply },
            };
        });
        return flyout;
    }

    // MARK: 聲音

    /// <summary>手機音量：媒體音量滑桿、一鍵靜音媒體、鈴聲模式（靜音／震動／響鈴），全部走 adb shell（對應 SoundState）</summary>
    private sealed class SoundState
    {
        public int Volume, MaxVolume = 15, Ringer = 2;       // Ringer：0 靜音、1 震動、2 響鈴
        public bool Dnd; public string Message = "";
        private int _lastNonZero = 8;
        private readonly Device _device;
        public SoundState(Device d) => _device = d;
        public bool IsMuted => Volume == 0;
        public int DisplayVolume => (int)Math.Round(Volume / (double)Math.Max(MaxVolume, 1) * 150);
        public bool IsLoud => DisplayVolume > 120;
        private async Task<string> Sh(string c) { try { return await _device.Client.ShellAsync(c); } catch { return ""; } }

        public async Task LoadAsync()
        {
            var o = await Sh("cmd media_session volume --stream 3 --get 2>&1");
            var line = o.Split('\n').LastOrDefault(l => l.Contains("volume is"));
            if (line is not null)
            {
                var nums = new string(line.Select(ch => char.IsDigit(ch) ? ch : ' ').ToArray()).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                if (nums.Length >= 3) { Volume = nums[0]; MaxVolume = Math.Max(nums[2], 1); if (Volume > 0) _lastNonZero = Volume; }
            }
            if (await CurrentRingerAsync() is int r) Ringer = r;
            var z = await Sh("settings get global zen_mode");
            Dnd = (int.TryParse(z.Trim(), out var zi) ? zi : 0) != 0;
        }

        /// <summary>請勿打擾：開 = 只允許重要的打擾（priority），關 = 全部通知</summary>
        public async Task SetDndAsync(bool on)
        {
            Dnd = on;
            await Sh($"cmd notification set_dnd {(on ? "priority" : "off")}");
            await Task.Delay(700);
            var z = await Sh("settings get global zen_mode");
            Dnd = (int.TryParse(z.Trim(), out var zi) ? zi : 0) != 0;
            if (await CurrentRingerAsync() is int r) Ringer = r;
        }

        public async void SetVolume(int v)
        {
            v = Math.Clamp(v, 0, MaxVolume);
            Volume = v; if (v > 0) _lastNonZero = v;
            await Sh($"cmd media_session volume --stream 3 --set {v}");
        }

        public void ToggleMute() => SetVolume(IsMuted ? _lastNonZero : 0);

        private async Task<int?> CurrentRingerAsync()
        {
            var o = await Sh("dumpsys audio | grep -m1 'mode (external)'");
            if (o.Contains("SILENT")) return 0;
            if (o.Contains("VIBRATE")) return 1;
            if (o.Contains("NORMAL")) return 2;
            return null;
        }

        /// <summary>依序嘗試幾種做法，每次都回頭確認手機真的切過去了（各廠牌、各版本的行為不一樣）</summary>
        public async Task SetRingerAsync(int mode)
        {
            int old = Ringer; Ringer = mode; Message = "";
            string[] names = ["SILENT", "VIBRATE", "NORMAL"];
            string[] attempts =
            [
                $"cmd audio set-ringer-mode {names[mode]} 2>&1",
                $"service call audio 38 i32 {mode} s16 com.android.shell",
                mode == 2 ? "cmd media_session volume --stream 2 --set 4" : "cmd media_session volume --stream 2 --set 0",
            ];
            foreach (var cmd in attempts)
            {
                await Sh(cmd);
                await Task.Delay(700);
                if (await CurrentRingerAsync() == mode) return;
            }
            Ringer = await CurrentRingerAsync() ?? old;
            Message = "手機沒有接受這個模式（可能被請勿打擾或系統限制擋住）";
        }
    }

    public static Flyout Sound(MirrorModel model) => MakeFlyout(() =>
    {
        var st = new SoundState(model.Device);
        var root = new StackPanel { Width = 290, Spacing = 16 };
        root.Children.Add(Ui.Ui.Text("聲音", 16, false, true));

        var vol = new Slider { Minimum = 0, Maximum = 15, StepFrequency = 1 };
        var muteIcon = new MIcon("volume_up", 20);
        var mute = new Button { Content = muteIcon, Background = null, BorderThickness = new Thickness(0), Padding = new Thickness(4) };
        var volText = Ui.Ui.Text("", 13, true); volText.Width = 40; volText.TextAlignment = TextAlignment.Right;
        var warn = new MIcon("warning", 16) { Foreground = Ui.Ui.Hex(0xFFFF453A), Visibility = Visibility.Collapsed };
        bool loading = true;
        void Paint()
        {
            loading = true;
            vol.Maximum = st.MaxVolume; vol.Value = st.Volume;
            muteIcon.Content = null;
            volText.Text = st.DisplayVolume.ToString();
            volText.Foreground = st.IsLoud ? Ui.Ui.Hex(0xFFFF453A) : Ui.Ui.Secondary;
            volText.FontWeight = st.IsLoud ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            warn.Visibility = st.IsLoud ? Visibility.Visible : Visibility.Collapsed;
            mute.Content = new MIcon(st.IsMuted ? "volume_off" : "volume_up", 20);
            ToolTipService.SetToolTip(mute, st.IsMuted ? "取消靜音" : "一鍵靜音");
            loading = false;
        }
        vol.ValueChanged += (_, e) => { if (!loading) { st.SetVolume((int)Math.Round(e.NewValue)); Paint(); } };
        mute.Click += (_, _) => { st.ToggleMute(); Paint(); };
        var volRow = new Grid { ColumnSpacing = 8 };
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        volRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        volRow.Children.Add(mute);
        Grid.SetColumn(vol, 1); volRow.Children.Add(vol);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Children = { warn, volText } };
        Grid.SetColumn(right, 2); volRow.Children.Add(right);

        var volBox = new StackPanel { Spacing = 10 };
        volBox.Children.Add(Ui.Ui.Text("媒體音量", 13, true));
        volBox.Children.Add(volRow);
        if (!model.IsVirtualDisplay)
        {
            bool audio = AppSettings.Data.AudioEnabled;
            var fromPc = new ToggleSwitch { Header = null, OnContent = "從電腦播放", OffContent = "從電腦播放", IsOn = !model.Muted, IsEnabled = audio };
            fromPc.Toggled += (_, _) => { if (fromPc.IsOn == model.Muted) model.ToggleMute(); };
            ToolTipService.SetToolTip(fromPc, audio ? "把手機的聲音傳到電腦播放" : "要先在「設定 → 鏡像」開啟「把手機聲音傳到電腦」");
            var fromPhone = new ToggleSwitch { OnContent = "從手機播放", OffContent = "從手機播放", IsOn = !AppSettings.Data.MuteDeviceWhenForwarding, IsEnabled = audio };
            fromPhone.Toggled += (_, _) =>
            {
                // 傳到電腦時手機也同時發聲：音源在連線時決定，所以切換會重新連線
                AppSettings.Update(d => d.MuteDeviceWhenForwarding = !fromPhone.IsOn);
                model.PendingChange("正在變更聲音輸出…");
                model.Restart();
            };
            ToolTipService.SetToolTip(fromPhone, "傳到電腦時，手機也同時發聲（需要 Android 13 以上）");
            volBox.Children.Add(fromPc); volBox.Children.Add(fromPhone);
        }
        root.Children.Add(volBox);

        var ringerBox = new StackPanel { Spacing = 8 };
        ringerBox.Children.Add(Ui.Ui.Text("鈴聲模式", 13, true));
        var seg = new SegmentedBar(["靜音", "震動", "響鈴"]);
        var msg = Ui.Ui.Text("", 12, true); msg.TextWrapping = TextWrapping.Wrap;
        var dnd = new ToggleSwitch { OnContent = "請勿打擾", OffContent = "請勿打擾" };
        bool ringerLoading = true;
        seg.SelectionChanged += async i => { if (ringerLoading) return; await st.SetRingerAsync(i); ringerLoading = true; seg.Selected = st.Ringer; ringerLoading = false; msg.Text = st.Message; };
        dnd.Toggled += async (_, _) => { if (!ringerLoading) { await st.SetDndAsync(dnd.IsOn); ringerLoading = true; dnd.IsOn = st.Dnd; seg.Selected = st.Ringer; ringerLoading = false; } };
        ringerBox.Children.Add(seg); ringerBox.Children.Add(msg); ringerBox.Children.Add(dnd);
        root.Children.Add(ringerBox);

        Paint();
        _ = Task.Run(async () =>
        {
            await st.LoadAsync();
            root.DispatcherQueue.TryEnqueue(() => { Paint(); ringerLoading = true; seg.Selected = st.Ringer; dnd.IsOn = st.Dnd; ringerLoading = false; });
        });
        return root;
    });

    // MARK: 桌面模式的小型啟動台

    public static Flyout MiniLauncher(MirrorModel model, Action<string> onPick)
    {
        Flyout? f = null;
        f = MakeFlyout(() =>
        {
            var catalog = model.Catalog;
            var search = new TextBox { PlaceholderText = "搜尋 App" };
            var grid = new ItemsRepeater { Layout = new UniformGridLayout { MinItemWidth = 72, MinItemHeight = 72, MinColumnSpacing = 8, MinRowSpacing = 12, ItemsJustification = UniformGridLayoutItemsJustification.Start } };
            void Fill()
            {
                var q = search.Text.Trim();
                grid.ItemsSource = catalog.Apps.Where(a => q.Length == 0 || a.Label.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Select(a =>
                {
                    var cell = new AppCell(catalog, a.Package, a.Label, 44, 72);
                    cell.Tapped += (_, _) => { f!.Hide(); onPick(a.Package); };
                    return (UIElement)cell;
                }).ToList();
            }
            search.TextChanged += (_, _) => Fill();
            Fill();
            if (catalog.Apps.Count == 0) _ = catalog.ReloadAsync().ContinueWith(_ => grid.DispatcherQueue.TryEnqueue(Fill));
            return new StackPanel { Width = 420, Spacing = 8, Children = { search, new ScrollViewer { Height = 360, Content = grid } } };
        });
        return f;
    }

    // MARK: 解鎖密碼

    /// <summary>設定手機解鎖密碼（PIN／密碼或圖形，用 Windows 帳戶加密保存）</summary>
    public static async Task ShowPasswordDialogAsync(Device device, XamlRoot root)
    {
        bool usePattern = false;
        var sequence = new List<int>();
        var pw = new PasswordBox { PlaceholderText = "PIN 或密碼" };
        var pad = new PatternPad(sequence) { HorizontalAlignment = HorizontalAlignment.Center };
        var patternText = Ui.Ui.Text("在上面畫出圖形（至少 4 個點）", 12, true);
        var warn = Ui.Ui.Text("圖形解鎖是 Beta 功能：圖形位置由畫面結構推算，部分機型（例如沒有螢幕下指紋的 Galaxy S8 等舊機，圖形在比較下面）可能畫不到，解鎖會失敗。", 12);
        warn.Foreground = Ui.Ui.Hex(0xFFFF9F0A); warn.TextWrapping = TextWrapping.Wrap; warn.MaxWidth = 320;
        var patternPanel = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed, Children = { pad, warn, patternText } };
        var clear = new Button { Content = "清除" }; clear.Click += (_, _) => { pad.Reset(); patternText.Text = "在上面畫出圖形（至少 4 個點）"; };
        patternPanel.Children.Add(clear);
        pad.SequenceChanged += () => patternText.Text = sequence.Count == 0 ? "在上面畫出圖形（至少 4 個點）" : string.Join(" → ", sequence);
        var seg = new SegmentedBar(["PIN／密碼", "圖形 (Beta)"]);
        seg.SelectionChanged += i => { usePattern = i == 1; patternPanel.Visibility = usePattern ? Visibility.Visible : Visibility.Collapsed; pw.Visibility = usePattern ? Visibility.Collapsed : Visibility.Visible; };
        var note = Ui.Ui.Text("密碼會存在這台電腦（用 Windows 帳戶加密）。偵測到手機在鎖定畫面時，可用 Windows Hello 驗證後自動填入。", 13, true);
        note.TextWrapping = TextWrapping.Wrap;
        var dialog = new ContentDialog
        {
            Title = $"「{device.DisplayName}」的解鎖密碼", XamlRoot = root, PrimaryButtonText = "儲存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Width = 340, Spacing = 12, Children = { note, seg, pw, patternPanel } },
        };
        if (LockScreenHelper.HasPassword(device)) dialog.SecondaryButtonText = "刪除已儲存的密碼";
        dialog.PrimaryButtonClick += (_, e) =>
        {
            if (usePattern ? sequence.Count < 4 : pw.Password.Length == 0) { e.Cancel = true; return; }
        };
        var r = await dialog.ShowAsync();
        if (r == ContentDialogResult.Primary)
            LockScreenHelper.SetPassword(usePattern ? LockScreenHelper.PatternPrefix + string.Join("", sequence) : pw.Password, device);
        else if (r == ContentDialogResult.Secondary) LockScreenHelper.SetPassword(null, device);
    }
}

/// <summary>3×3 圖形密碼輸入：拖曳經過圓點，點的編號 1–9（左上到右下）</summary>
internal sealed class PatternPad : UserControl
{
    private readonly List<int> _seq;
    private readonly Canvas _canvas = new() { Width = 180, Height = 180 };
    private (double X, double Y)? _current;
    private const double Size = 180;
    public event Action? SequenceChanged;

    public PatternPad(List<int> sequence)
    {
        _seq = sequence;
        Content = new Border { Child = _canvas, Background = Ui.Ui.Hex(0x1AFFFFFF), CornerRadius = new CornerRadius(14), Width = Size, Height = Size };
        PointerPressed += (_, e) => { CapturePointer(e.Pointer); _seq.Clear(); Add(e); };
        PointerMoved += (_, e) => { if (e.Pointer.IsInContact || e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) Add(e); };
        PointerReleased += (_, e) => { ReleasePointerCapture(e.Pointer); _current = null; Draw(); };
        Draw();
    }

    public void Reset() { _seq.Clear(); _current = null; Draw(); SequenceChanged?.Invoke(); }

    private static (double X, double Y) Center(int n) => (Size / 3 * ((n - 1) % 3 + 0.5), Size / 3 * ((n - 1) / 3 + 0.5));

    private void Add(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_canvas).Position;
        _current = (p.X, p.Y);
        for (int n = 1; n <= 9; n++)
        {
            var c = Center(n);
            if (Math.Sqrt((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y)) < Size / 9 && !_seq.Contains(n)) { _seq.Add(n); SequenceChanged?.Invoke(); }
        }
        Draw();
    }

    private void Draw()
    {
        _canvas.Children.Clear();
        if (_seq.Count > 0)
        {
            var line = new Polyline { Stroke = Ui.Ui.Accent, StrokeThickness = 4, StrokeLineJoin = Microsoft.UI.Xaml.Media.PenLineJoin.Round, StrokeStartLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round, StrokeEndLineCap = Microsoft.UI.Xaml.Media.PenLineCap.Round };
            foreach (var n in _seq) { var c = Center(n); line.Points.Add(new Windows.Foundation.Point(c.X, c.Y)); }
            if (_current is { } cur) line.Points.Add(new Windows.Foundation.Point(cur.X, cur.Y));
            _canvas.Children.Add(line);
        }
        for (int n = 1; n <= 9; n++)
        {
            bool on = _seq.Contains(n); double d = on ? 16 : 12; var c = Center(n);
            var dot = new Ellipse { Width = d, Height = d, Fill = on ? Ui.Ui.Accent : Ui.Ui.Hex(0x59808080) };
            Canvas.SetLeft(dot, c.X - d / 2); Canvas.SetTop(dot, c.Y - d / 2);
            _canvas.Children.Add(dot);
        }
    }
}
