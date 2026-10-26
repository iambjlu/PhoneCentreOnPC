namespace PhoneCentre.Mirror;

/// <summary>鏡像視窗的版面常數（單位 DIP，對應 MirrorLayout）</summary>
public static class MirrorLayout
{
    /// <summary>右側欄（狀態指示、App 列、導覽列）寬度；視窗上方沒有工具列</summary>
    public const double RailWidth = 76;
    public const double ToolbarHeight = 0;
    /// <summary>主螢幕鏡像在直向時的視窗最小大小（含標題列）：364×636</summary>
    public const double PortraitMinFrameWidth = 364, PortraitMinFrameHeight = 636;
    /// <summary>橫向時的視窗最小大小（含標題列）：636×364</summary>
    public const double LandscapeMinFrameWidth = 636, LandscapeMinFrameHeight = 364;
    /// <summary>視窗內容高度低於這個值（視窗高度未滿 636）時，右側欄整排移到視窗底部</summary>
    public const double CompactContentHeight = 608;
    public const double BottomBarHeight = 64;
}
