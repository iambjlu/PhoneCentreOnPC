using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Ui;

namespace PhoneCentre.Pages;

/// <summary>加入裝置（對應 AddDeviceSheet.swift 的 WirelessAddView）：USB 轉無線、adb connect、無線偵錯配對、mDNS 找到的裝置。遠端 PhoneCentre 暫時不做。</summary>
public static class AddDeviceDialog
{
    public static async Task ShowAsync(XamlRoot root)
    {
        var dm = DeviceManager.Shared;
        var message = new TextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Ui.Secondary, FontSize = 13 };
        var address = new TextBox { PlaceholderText = "IP:Port，例如 172.20.10.2:5555", MinWidth = 280 };
        var pairAddress = new TextBox { PlaceholderText = "配對 IP:Port（手機上「使用配對碼配對裝置」顯示的）" };
        var pairCode = new TextBox { PlaceholderText = "六位數配對碼", Width = 160 };
        var body = new StackPanel { Spacing = 16, Width = 480 };
        bool busy = false;

        async Task Connect(string addr)
        {
            if (busy || addr.Trim().Length == 0) return;
            busy = true; message.Text = "連線中…";
            message.Text = (await dm.ConnectAsync(addr)).Message;
            busy = false;
        }

        // USB 轉無線
        FrameworkElement[] UsbRows()
        {
            var usb = dm.OnlineDevices.Where(d => !d.IsWireless && !d.IsRemote).ToList();
            if (usb.Count == 0) return [Form.Note("用 USB 線接上手機（已開啟 USB 偵錯），就能一鍵切換成無線連線。")];
            return usb.Select(d =>
            {
                var left = new StackPanel { Children = { Ui.Ui.Text(d.DisplayName, 14), Ui.Ui.Text("執行 adb tcpip 5555 後自動連線", 12, true) } };
                var b = new Button { Content = "切換為無線" };
                b.Click += async (_, _) => { if (busy) return; busy = true; message.Text = "切換中…"; message.Text = (await dm.EnableWirelessAsync(d)).Message; busy = false; };
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(left); Grid.SetColumn(b, 1); g.Children.Add(b);
                return (FrameworkElement)g;
            }).ToArray();
        }
        body.Children.Add(Form.Section("USB 轉無線", UsbRows()));

        // adb connect
        var connect = new Button { Content = "連線" };
        connect.Click += async (_, _) => await Connect(address.Text);
        address.KeyDown += async (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) await Connect(address.Text); };
        var connectRow = new Grid { ColumnSpacing = 8 };
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        connectRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        connectRow.Children.Add(address); Grid.SetColumn(connect, 1); connectRow.Children.Add(connect);
        body.Children.Add(Form.Section("連線（adb connect）", connectRow, Form.Note("連線成功的裝置會被記住；adb 被關掉或網路斷線後會自動重連。")));

        // 配對
        var discovered = new StackPanel { Spacing = 0 };
        async Task Scan()
        {
            var list = await AdbClient.MdnsServicesAsync();
            discovered.Children.Clear();
            if (list.Count == 0) discovered.Children.Add(Form.Note("沒有找到。請確認手機已開啟「無線偵錯」且和這台電腦在同一個網路。"));
            foreach (var s in list)
            {
                var left = new StackPanel { Children = { Ui.Ui.Text(s.Instance, 14), Ui.Ui.Text($"{s.Address} · {(s.Type.Contains("pairing") ? "等待配對" : "可連線")}", 12, true) } };
                var b = new Button { Content = s.Type.Contains("pairing") ? "填入配對位址" : "連線" };
                var addr = s.Address; bool pairing = s.Type.Contains("pairing");
                b.Click += async (_, _) => { if (pairing) pairAddress.Text = addr; else { address.Text = addr; await Connect(addr); } };
                var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(left); Grid.SetColumn(b, 1); g.Children.Add(b);
                discovered.Children.Add(g);
            }
        }
        var pair = new Button { Content = "配對" };
        pair.Click += async (_, _) =>
        {
            if (busy || pairAddress.Text.Trim().Length == 0 || pairCode.Text.Trim().Length == 0) return;
            busy = true; message.Text = "配對中…";
            message.Text = (await AdbClient.PairAsync(pairAddress.Text.Trim(), pairCode.Text.Trim())).Message;
            busy = false; await Scan();
        };
        var codeRow = new Grid { ColumnSpacing = 8 };
        codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        codeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        codeRow.Children.Add(pairCode); Grid.SetColumn(pair, 1); codeRow.Children.Add(pair);
        pairCode.HorizontalAlignment = HorizontalAlignment.Left;
        body.Children.Add(Form.Section("配對（Android 11 以上的「無線偵錯」）", pairAddress, codeRow));

        // 區域網路上找到的裝置
        var refresh = new Button { Content = "重新整理" };
        refresh.Click += async (_, _) => await Scan();
        var header = new Grid { Margin = new Thickness(4, 0, 0, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Ui.Text("在區域網路上找到的裝置", 13, true, true)); Grid.SetColumn(refresh, 1); header.Children.Add(refresh);
        var found = Form.Section(null, discovered);
        body.Children.Add(new StackPanel { Spacing = 6, Children = { header, found } });
        body.Children.Add(message);

        var dialog = new ContentDialog
        {
            Title = "加入裝置", Content = new ScrollViewer { Content = body, MaxHeight = 560, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "完成", XamlRoot = root, DefaultButton = ContentDialogButton.Close,
        };
        _ = Scan();
        await dialog.ShowAsync();
    }
}
