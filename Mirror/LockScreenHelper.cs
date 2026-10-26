using Windows.Foundation;
using PhoneCentre.Adb;
using PhoneCentre.Services;
using PhoneCentre.Settings;
using PhoneCentre.Support;

namespace PhoneCentre.Mirror;

/// <summary>偵測手機鎖定畫面，並用 Windows Hello 驗證後自動輸入儲存的手機密碼（對應 LockScreenHelper.swift）</summary>
public static class LockScreenHelper
{
    public static string SecretKey(Device d) => $"lock.{d.Key}";
    public static bool HasPassword(Device d) => AppSettings.Secret(SecretKey(d)) is not null;
    public static void SetPassword(string? pw, Device d) => AppSettings.SetSecret(SecretKey(d), pw);

    /// <summary>是否在鎖定畫面（keyguard 顯示中）</summary>
    public static async Task<bool> IsLockedAsync(AdbClient c)
    {
        string outp;
        try
        {
            outp = await c.ShellAsync(
                "dumpsys window | grep -E 'mDreamingLockscreen=|mShowingLockscreen=|isKeyguardShowing=|mKeyguardShowing='; " +
                "dumpsys activity activities | grep -iE 'keyguardShowing=' | head -3", 5);
        }
        catch { return false; }
        var l = outp.ToLowerInvariant();
        return l.Contains("dreaminglockscreen=true") || l.Contains("showinglockscreen=true") || l.Contains("keyguardshowing=true");
    }

    /// <summary>
    /// Windows Hello（指紋、臉部或 PIN）驗證。沒有設定 Hello 的電腦退回「確認對話框」。
    /// 注意：這是同一個使用者帳戶內的保護，儲存的密碼本來就只有這個帳戶解得開（DPAPI）。
    /// </summary>
    public static async Task<bool> AuthenticateAsync(string reason, Microsoft.UI.Xaml.XamlRoot? root)
    {
        try
        {
            var avail = await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync();
            if (avail == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)
            {
                var r = await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync(reason);
                return r == Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified;
            }
        }
        catch (Exception e) { Log.Write("Windows Hello 驗證失敗 " + e.Message); }
        if (root is null) return true;
        var dlg = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = reason, Content = "這台電腦沒有設定 Windows Hello，改用確認。", PrimaryButtonText = "填入", CloseButtonText = "取消", XamlRoot = root,
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Primary,
        };
        return await dlg.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
    }

    public const string PatternPrefix = "pattern:";
    public static bool IsPattern(string pw) => pw.StartsWith(PatternPrefix);
    public static List<int> PatternSequence(string pw) => pw[PatternPrefix.Length..].Where(char.IsDigit).Select(c => c - '0').ToList();

    /// <summary>螢幕大小（像素），用來算上滑與圖形密碼的座標</summary>
    private static (double W, double H) ScreenSize(Device d)
    {
        var p = (d.Info?.ScreenSize ?? "").Split('x').Select(x => double.TryParse(x, out var v) ? v : 0).ToArray();
        return p.Length == 2 && p[0] > 0 && p[1] > 0 ? (Math.Min(p[0], p[1]), Math.Max(p[0], p[1])) : (1080, 2280);
    }

    /// <summary>用 uiautomator 找圖形密碼的範圍；找不到就用螢幕下半部推測</summary>
    private static async Task<(double X, double Y, double W, double H)> PatternBoundsAsync(AdbClient c, (double W, double H) screen)
    {
        string xml = "";
        try { xml = await c.ShellAsync("uiautomator dump /sdcard/pc_ui.xml >/dev/null 2>&1; cat /sdcard/pc_ui.xml; rm -f /sdcard/pc_ui.xml", 15); } catch { }
        foreach (var node in xml.Split("<node "))
        {
            if (!node.Contains("lockpatternview", StringComparison.OrdinalIgnoreCase)) continue;
            int i = node.IndexOf("bounds=\"[", StringComparison.Ordinal);
            if (i < 0) continue;
            var nums = new string(node[(i + 8)..].TakeWhile(ch => ch != '"').ToArray())
                .Split(['[', ']', ','], StringSplitOptions.RemoveEmptyEntries).Select(x => double.TryParse(x, out var v) ? v : -1).ToArray();
            if (nums.Length == 4 && nums[2] > nums[0] && nums[3] > nums[1]) return (nums[0], nums[1], nums[2] - nums[0], nums[3] - nums[1]);
        }
        double w = screen.W * 0.8;
        return ((screen.W - w) / 2, screen.H * 0.85 - w, w, w);
    }

    /// <summary>圖形密碼每一點的座標（螢幕像素），每兩點之間補幾個點讓軌跡連續</summary>
    private static List<(double X, double Y)> PatternPath(List<int> seq, (double X, double Y, double W, double H) b)
    {
        (double, double) Pt(int n) => (b.X + b.W * (((n - 1) % 3) + 0.5) / 3, b.Y + b.H * (((n - 1) / 3) + 0.5) / 3);
        var pts = new List<(double X, double Y)>();
        if (seq.Count == 0) return pts;
        pts.Add(Pt(seq[0]));
        for (int i = 1; i < seq.Count; i++)
        {
            var (x0, y0) = Pt(seq[i - 1]); var (x1, y1) = Pt(seq[i]);
            for (int k = 1; k <= 5; k++) { double t = k / 5.0; pts.Add((x0 + (x1 - x0) * t, y0 + (y1 - y0) * t)); }
        }
        pts.Add(pts[^1]);
        return pts;
    }

    /// <summary>畫圖形：有主螢幕鏡像連線就直接送觸控（最快）；沒有就由手機端輔助程式在同一個程序內連續注入，失敗才退回 input motionevent</summary>
    private static async Task DrawPatternAsync(List<int> seq, Device device, ScrcpySession? session, (double W, double H) screen, (double X, double Y, double W, double H) b)
    {
        var pts = PatternPath(seq, b);
        if (pts.Count == 0) return;
        if (session is { FrameSize.Width: > 0 } s)
        {
            var (fw, fh) = s.FrameSize;
            double kx = fw / screen.W, ky = fh / screen.H;
            for (int i = 0; i < pts.Count; i++)
            {
                var action = i == 0 ? ControlMessage.MotionAction.Down : i == pts.Count - 1 ? ControlMessage.MotionAction.Up : ControlMessage.MotionAction.Move;
                s.Send(ControlMessage.Touch(action, ControlMessage.PointerFinger, (int)(pts[i].X * kx), (int)(pts[i].Y * ky), (ushort)fw, (ushort)fh, action == ControlMessage.MotionAction.Up ? 0f : 1f));
                await Task.Delay(8);
            }
            return;
        }
        var arg = pts.Select(p => $"{(int)p.X},{(int)p.Y}").ToList();
        string outp = "";
        try { outp = await HelperTool.Shared.RunAsync(device, ["gesture", "8", .. arg], 15); } catch { }
        if (outp.Contains("ok")) return;
        var cmds = new List<string> { "input motionevent DOWN " + arg[0].Replace(',', ' ') };
        cmds.AddRange(arg.Skip(1).Take(arg.Count - 2).Select(a => "input motionevent MOVE " + a.Replace(',', ' ')));
        cmds.Add("input motionevent UP " + arg[^1].Replace(',', ' '));
        try { await device.Client.ShellAsync(string.Join("; ", cmds), 20); } catch { }
    }

    /// <summary>找到過的圖形密碼範圍（依裝置），下次不用再跑一次很慢的 uiautomator；解鎖失敗就清掉重找</summary>
    private static readonly Dictionary<string, (double X, double Y, double W, double H)> PatternCache = new();

    /// <summary>手機面板關閉／開啟（手機本身保持開機）：自動填入密碼期間黑屏，旁人看不到圖形或數字</summary>
    private static async Task SetPanelAsync(bool on, Device device, ScrcpySession? session)
    {
        if (session is not null) { session.Send(ControlMessage.SetDisplayPower(on)); return; }
        try { await HelperTool.Shared.RunAsync(device, ["power", on ? "2" : "0"], 10); } catch { }
    }

    /// <summary>喚醒 → 上滑 → 輸入密碼（或畫圖形）→ Enter。獨立視窗的鏡像連線不能操作主螢幕，所以傳 session = null 改走 adb input。</summary>
    public static async Task<bool> UnlockAsync(Device device, ScrcpySession? session, bool keepScreenOff, Microsoft.UI.Xaml.XamlRoot? root)
    {
        if (!await AuthenticateAsync($"填入「{device.DisplayName}」的解鎖密碼", root) || AppSettings.Secret(SecretKey(device)) is not { } pw) return false;
        var c = device.Client;
        try { await c.ShellAsync("input keyevent 224"); } catch { }
        // 自動填入期間手機黑屏（只關面板，手機保持開機），解鎖完再打開
        await SetPanelAsync(false, device, session);
        try
        {
            await Task.Delay(300);
            var screen = ScreenSize(device);
            if (session is { FrameSize.Width: > 0 } s)
            {
                // 透過鏡像連線上滑
                var (fw, fh) = s.FrameSize;
                int x = fw / 2; const int steps = 8;
                s.Send(ControlMessage.Touch(ControlMessage.MotionAction.Down, ControlMessage.PointerFinger, x, (int)(fh * 0.85), (ushort)fw, (ushort)fh, 1f));
                for (int i = 1; i <= steps; i++)
                {
                    await Task.Delay(15);
                    s.Send(ControlMessage.Touch(ControlMessage.MotionAction.Move, ControlMessage.PointerFinger, x, (int)(fh * (0.85 - 0.5 * i / steps)), (ushort)fw, (ushort)fh, 1f));
                }
                s.Send(ControlMessage.Touch(ControlMessage.MotionAction.Up, ControlMessage.PointerFinger, x, (int)(fh * 0.35), (ushort)fw, (ushort)fh, 0f));
            }
            else
            {
                int x = (int)(screen.W / 2);
                try { await c.ShellAsync($"wm dismiss-keyguard; input swipe {x} {(int)(screen.H * 0.85)} {x} {(int)(screen.H * 0.3)} 150"); } catch { }
            }
            await Task.Delay(700);
            var key = device.Key;
            if (IsPattern(pw))
            {
                var bounds = PatternCache.TryGetValue(key, out var cached) ? cached : await PatternBoundsAsync(c, screen);
                await DrawPatternAsync(PatternSequence(pw), device, session, screen, bounds);
                await Task.Delay(900);
                bool ok = !await IsLockedAsync(c);
                if (ok) PatternCache[key] = bounds; else PatternCache.Remove(key);
                return ok;
            }
            if (session is not null && pw.All(ch => ch < 128))
            {
                session.Send(ControlMessage.Text(pw));
                session.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Down, 66));
                session.Send(ControlMessage.Keycode(ControlMessage.KeyAction.Up, 66));
            }
            else
            {
                try { await c.ShellAsync($"input text {Shell.Quote(pw.Replace(" ", "%s"))}; input keyevent 66"); } catch { }
            }
            await Task.Delay(1200);
            return !await IsLockedAsync(c);
        }
        finally { if (!keepScreenOff) _ = SetPanelAsync(true, device, session); }
    }
}
