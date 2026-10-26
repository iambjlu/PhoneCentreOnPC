using Windows.System;

namespace PhoneCentre.Mirror;

/// <summary>Windows 虛擬按鍵（與鍵盤配置、輸入法無關的按鍵位置）→ Android keycode（對應 KeyMap.swift）</summary>
public static class KeyMap
{
    public const int Back = 4, Home = 3, AppSwitch = 187, Menu = 82, Enter = 66, Del = 67, ForwardDel = 112, Tab = 61, Space = 62;
    public const int Up = 19, Down = 20, Left = 21, Right = 22, MoveHome = 122, MoveEnd = 123, PageUp = 92, PageDown = 93;
    public const int VolumeUp = 24, VolumeDown = 25, Power = 26, Wakeup = 224;

    private static readonly Dictionary<int, int> Table = BuildTable();

    private static Dictionary<int, int> BuildTable()
    {
        var t = new Dictionary<int, int>();
        for (int i = 0; i < 26; i++) t[0x41 + i] = 29 + i;                 // A–Z
        for (int i = 0; i < 10; i++) t[0x30 + i] = 7 + i;                  // 0–9
        // 符號：- = [ ] \ ; ' , . / `
        foreach (var (vk, code) in new[] { (0xBD, 69), (0xBB, 70), (0xDB, 71), (0xDD, 72), (0xDC, 73), (0xBA, 74), (0xDE, 75), (0xBC, 55), (0xBE, 56), (0xBF, 76), (0xC0, 68) })
            t[vk] = code;
        t[0x0D] = Enter; t[0x08] = Del; t[0x2E] = ForwardDel; t[0x09] = Tab; t[0x20] = Space;
        t[0x1B] = Back;                                                     // Esc = 返回
        t[0x25] = Left; t[0x27] = Right; t[0x28] = Down; t[0x26] = Up;
        t[0x24] = MoveHome; t[0x23] = MoveEnd; t[0x21] = PageUp; t[0x22] = PageDown; t[0x2D] = 124;
        for (int i = 0; i < 12; i++) t[0x70 + i] = 131 + i;                // F1–F12
        for (int i = 0; i < 10; i++) t[0x60 + i] = 144 + i;                // 數字鍵盤 0–9
        t[0x6A] = 155; t[0x6B] = 157; t[0x6D] = 156; t[0x6E] = 158; t[0x6F] = 154;
        return t;
    }

    public static int? ForKey(VirtualKey k) => Table.TryGetValue((int)k, out var c) ? c : null;

    /// <summary>可以由輸入法產生文字的按鍵（字母、數字、符號、空白）：輸入法模式下交給輸入法，不直接送 keycode</summary>
    public static bool IsTextKey(VirtualKey k)
    {
        int v = (int)k;
        return (v >= 0x41 && v <= 0x5A) || (v >= 0x30 && v <= 0x39) || v == 0x20 || (v >= 0xBA && v <= 0xC0) || (v >= 0xDB && v <= 0xDE);
    }

    /// <summary>修飾鍵 → Android meta state</summary>
    public static int Meta(bool shift, bool alt, bool ctrl, bool caps)
    {
        int m = 0;
        if (shift) m |= 0x1 | 0x40;
        if (alt) m |= 0x2 | 0x10;
        if (ctrl) m |= 0x1000 | 0x2000;
        if (caps) m |= 0x100000;
        return m;
    }
}
