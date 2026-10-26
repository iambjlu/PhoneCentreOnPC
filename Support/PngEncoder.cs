using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PhoneCentre.Support;

/// <summary>把 BGRA 像素編成 PNG（截圖用，用系統內建的編碼器）</summary>
public static class PngEncoder
{
    public static async Task<byte[]> EncodeAsync(byte[] bgra, int width, int height)
    {
        using var stream = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, bgra);
        await enc.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
