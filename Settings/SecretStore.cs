using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PhoneCentre.Support;

namespace PhoneCentre.Settings;

/// <summary>
/// 密碼（手機解鎖密碼、分享密碼、遠端主機密碼）的本機保存，對應 Mac 版的鑰匙圈（Keychain.swift）。
/// 用 Windows 的 DPAPI 以目前使用者加密，存在 %LOCALAPPDATA%\PhoneCentre\secrets.json；別的使用者或別台電腦解不開。
/// </summary>
public static class SecretStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _cache;
    private static string FilePath => Path.Combine(Assets.SupportDir, "secrets.json");

    private static Dictionary<string, string> Load()
    {
        if (_cache is not null) return _cache;
        try { _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new(); }
        catch { _cache = new(); }
        return _cache;
    }

    public static string? Get(string key)
    {
        lock (Gate)
        {
            if (!Load().TryGetValue(key, out var b64)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(b64), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }
    }

    public static void Set(string key, string? value)
    {
        lock (Gate)
        {
            var d = Load();
            if (string.IsNullOrEmpty(value)) d.Remove(key);
            else d[key] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(d)); } catch (Exception e) { Log.Write("保存密碼失敗 " + e.Message); }
        }
    }
}
