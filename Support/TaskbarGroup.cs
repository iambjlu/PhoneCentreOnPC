using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using PhoneCentre.Settings;

namespace PhoneCentre.Support;

/// <summary>工作列群組（對應 Dock 圖示的「共用／每個視窗各自一個」）：給視窗不同的 AppUserModelID，工作列就會把它當成獨立的程式顯示。</summary>
public static class TaskbarGroup
{
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct PROPVARIANT { [FieldOffset(0)] public ushort vt; [FieldOffset(8)] public IntPtr p; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint c);
        void GetAt(uint i, out PROPERTYKEY k);
        void GetValue(ref PROPERTYKEY k, out PROPVARIANT v);
        void SetValue(ref PROPERTYKEY k, ref PROPVARIANT v);
        void Commit();
    }

    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT v);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string id);

    private const string Shared = "PhoneCentre.App";

    /// <summary>整個程式預設的工作列群組；要在建立任何視窗之前呼叫</summary>
    public static void Init() => SetCurrentProcessExplicitAppUserModelID(Shared);

    /// <summary>依設定決定這個視窗的工作列群組。key：每個視窗唯一的名稱；mirror：true 是手機鏡像視窗，false 是獨立 App／桌面視窗</summary>
    public static void Apply(Window window, string key, bool mirror)
    {
        try
        {
            var mode = mirror ? AppSettings.Data.MirrorTaskbarMode : AppSettings.Data.AppWindowTaskbarMode;
            Set(window, mode == TaskbarMode.Separate ? "PhoneCentre.Window." + key : Shared);
        }
        catch (Exception e) { Log.Write("設定工作列群組失敗 " + e.Message); }
    }

    private static void Set(Window window, string id)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var iid = typeof(IPropertyStore).GUID;
        if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0) return;
        var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };   // PKEY_AppUserModel_ID
        var v = new PROPVARIANT { vt = 31, p = Marshal.StringToCoTaskMemUni(id) };   // VT_LPWSTR，PropVariantClear 會釋放字串
        store.SetValue(ref key, ref v);
        store.Commit();
        PropVariantClear(ref v);
        Marshal.ReleaseComObject(store);
    }
}
