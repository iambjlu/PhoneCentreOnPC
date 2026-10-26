using System.Globalization;
using System.Text;

namespace PhoneCentre.Support;

/// <summary>啟動台的分組索引：英文字母 A–Z → 注音 ㄅ–ㄦ → 其他（#），對應 LaunchpadView.swift 的 AppIndex</summary>
public static class AppIndex
{
    public static readonly string[] Bopomofo = "ㄅㄆㄇㄈㄉㄊㄋㄌㄍㄎㄏㄐㄑㄒㄓㄔㄕㄖㄗㄘㄙㄧㄨㄩㄚㄛㄜㄝㄞㄟㄠㄡㄢㄣㄤㄥㄦ".Select(c => c.ToString()).ToArray();
    public static readonly string[] Order = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).Concat(Bopomofo).Append("#").ToArray();

    /// <summary>名稱排序：像 Finder 一樣把數字當數字比（App2 在 App10 前面），不分大小寫</summary>
    public static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);

    public static string Key(string label)
    {
        if (label.Length == 0) return "#";
        char c = label[0];
        if (c < 128) return char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : "#";
        int i = c - BopomofoTable.Start;
        if (i >= 0 && i < BopomofoTable.Keys.Length) return BopomofoTable.Keys[i].ToString();
        // 帶重音的拉丁字母（é → E）等：拆解後取第一個 ASCII 字母
        var d = c.ToString().Normalize(NormalizationForm.FormD);
        return d.Length > 0 && d[0] < 128 && char.IsLetter(d[0]) ? char.ToUpperInvariant(d[0]).ToString() : "#";
    }

    public static int Rank(string key)
    {
        int i = Array.IndexOf(Order, key);
        return i < 0 ? Order.Length : i;
    }
}
