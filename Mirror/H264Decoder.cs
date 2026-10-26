using System.Runtime.InteropServices;
using PhoneCentre.Support;
using static PhoneCentre.Mirror.FFmpegNative;

namespace PhoneCentre.Mirror;

/// <summary>
/// 用 FFmpeg（libavcodec）軟體解碼 scrcpy 的 H.264 Annex B 封包，轉成 BGRA。對應 scrcpy 本身的做法，
/// 各平台行為一致（系統內建的 Media Foundation 解碼器在 x64 上對這個串流沒有輸出）。
/// <see cref="Config"/>／<see cref="Decode"/> 在影像讀取執行緒呼叫；UI 用 <see cref="TryTakeFrame"/> 取最新一幀（不排隊，永遠只顯示最新的）。
/// </summary>
public sealed unsafe class H264Decoder : IDisposable
{
    private const int Padding = 256;
    private IntPtr _ctx, _pkt, _frame, _sws;
    private byte[] _config = [];
    private readonly object _gate = new();
    private byte[]? _front, _back;           // 雙緩衝：解碼寫 back，完成後與 front 交換
    private int _w, _h;
    private bool _fresh;
    private long _decoded;
    private bool _sawKey;

    public event Action<int, int>? SizeChanged;

    /// <summary>每個關鍵幀都保留一份（不是全黑才算），獨立視窗的 App 被關掉時拿來當霧化背景（只有獨立視窗開啟，省記憶體）</summary>
    public bool KeepLastFrame { get; set; }
    private (byte[] Bgra, int W, int H)? _lastGood;
    private bool _curKey;
    private Action<byte[], int, int>? _capture;

    public (byte[] Bgra, int W, int H)? LastGoodFrame { get { lock (_gate) return _lastGood; } }

    /// <summary>下一個關鍵幀解出來時呼叫（背景執行緒）一次，給截圖用</summary>
    public void CaptureNextKeyFrame(Action<byte[], int, int> done) { lock (_gate) _capture = done; }

    public H264Decoder()
    {
        EnsureLoaded();
        var codec = avcodec_find_decoder(AV_CODEC_ID_H264);
        if (codec == IntPtr.Zero) throw new InvalidOperationException("FFmpeg 沒有 H.264 解碼器");
        _ctx = avcodec_alloc_context3(codec);
        // 低延遲：不為了重排而緩衝；用 slice 執行緒（frame 執行緒會多出幾幀延遲）
        av_opt_set(_ctx, "flags", "+low_delay", 1);
        av_opt_set(_ctx, "thread_type", "slice", 1);
        av_opt_set_int(_ctx, "threads", Math.Min(Environment.ProcessorCount, 4), 1);
        int r = avcodec_open2(_ctx, codec, IntPtr.Zero);
        if (r < 0) throw new InvalidOperationException("無法開啟 H.264 解碼器：" + Error(r));
        _pkt = av_packet_alloc();
        _frame = av_frame_alloc();
    }

    public void Config(byte[] packet) => _config = packet;

    /// <summary>解一個影像封包；關鍵幀前面補上 SPS/PPS（scrcpy 的 config 封包獨立送來）</summary>
    public void Decode(byte[] packet, bool key)
    {
        if (!_sawKey && !key) return;
        _sawKey = true;
        _curKey = key;
        int extra = key ? _config.Length : 0;
        int r = av_new_packet(_pkt, packet.Length + extra);
        if (r < 0) return;
        byte* dst = PacketData(_pkt);
        if (extra > 0) Marshal.Copy(_config, 0, (IntPtr)dst, extra);
        Marshal.Copy(packet, 0, (IntPtr)(dst + extra), packet.Length);
        r = avcodec_send_packet(_ctx, _pkt);
        av_packet_unref(_pkt);
        if (r < 0) { Log.Write("send_packet " + Error(r)); return; }

        while (true)
        {
            r = avcodec_receive_frame(_ctx, _frame);
            if (r == AVERROR_EAGAIN || r < 0) { if (r != AVERROR_EAGAIN) Log.Write("receive_frame " + Error(r)); break; }
            Convert();
        }
    }

    private void Convert()
    {
        int w = FrameWidth(_frame), h = FrameHeight(_frame), fmt = FrameFormat(_frame);
        _sws = sws_getCachedContext(_sws, w, h, fmt, w, h, AV_PIX_FMT_BGRA, SWS_BILINEAR, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_sws == IntPtr.Zero) { Log.Write($"sws 不支援像素格式 {fmt}"); return; }

        // swscale 的 SIMD 在畫面寬度不是 16 的倍數時，可能會在緩衝區尾端多寫幾個位元組（FFmpeg 要求輸出緩衝區要有填充），
        // 沒留的話會寫壞 GC 堆，整個程式以「執行階段內部錯誤」崩潰（虛擬顯示器的寬度常常不是 16 的倍數）
        int size = w * h * 4 + Padding;
        lock (_gate) { if (_back is null || _back.Length != size) _back = new byte[size]; }
        fixed (byte* outp = _back)
        {
            byte** dstData = stackalloc byte*[4];
            int* dstStride = stackalloc int[4];
            dstData[0] = outp; dstStride[0] = w * 4;
            sws_scale(_sws, FrameData(_frame), FrameLinesize(_frame), 0, h, dstData, dstStride);
        }
        bool sizeChanged;
        lock (_gate)
        {
            (_front, _back) = (_back, _front);
            sizeChanged = w != _w || h != _h;
            (_w, _h) = (w, h);
            _fresh = true;
        }
        if (_curKey)
        {
            Action<byte[], int, int>? cap; byte[]? copy = null;
            lock (_gate)
            {
                cap = _capture; _capture = null;
                if (cap is not null || KeepLastFrame) copy = _front.AsSpan(0, w * h * 4).ToArray();
                if (KeepLastFrame && copy is not null && !IsMostlyBlack(copy)) _lastGood = (copy, w, h);
            }
            if (cap is not null && copy is not null) cap(copy, w, h);
        }
        if (++_decoded <= 2) Log.Write($"解出第 {_decoded} 幀 {w}x{h} fmt={fmt}");
        if (sizeChanged) SizeChanged?.Invoke(w, h);
    }

    /// <summary>取樣檢查是不是（幾乎）全黑：App 剛關掉時的黑畫面不能拿來當「最後一幀」</summary>
    private static bool IsMostlyBlack(byte[] bgra)
    {
        long sum = 0; int n = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4 * 97) { sum += bgra[i] + bgra[i + 1] + bgra[i + 2]; n++; }
        return n == 0 || sum / (3.0 * n) < 6;
    }

    /// <summary>取最新一幀（沒有新的就回傳 false）。回傳的陣列只在下一次解碼前有效，請立刻複製</summary>
    public bool TryTakeFrame(Action<byte[], int, int> consume)
    {
        lock (_gate)
        {
            if (!_fresh || _front is null) return false;
            consume(_front, _w, _h);
            _fresh = false;
            return true;
        }
    }

    public void Dispose()
    {
        if (_sws != IntPtr.Zero) { sws_freeContext(_sws); _sws = IntPtr.Zero; }
        if (_frame != IntPtr.Zero) av_frame_free(ref _frame);
        if (_pkt != IntPtr.Zero) av_packet_free(ref _pkt);
        if (_ctx != IntPtr.Zero) avcodec_free_context(ref _ctx);
    }
}
