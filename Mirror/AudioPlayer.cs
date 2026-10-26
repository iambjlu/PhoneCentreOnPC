using NAudio.Wave;
using NAudio.CoreAudioApi;
using PhoneCentre.Support;

namespace PhoneCentre.Mirror;

/// <summary>播放 scrcpy 的 raw 音訊（PCM s16le、雙聲道、48 kHz）：WASAPI 共用模式，用緩衝區控制延遲（對應 AudioPlayer.swift）</summary>
public sealed class AudioPlayer : IDisposable
{
    private WasapiOut? _out;
    private readonly BufferedWaveProvider _buffer = new(new WaveFormat(48_000, 16, 2)) { DiscardOnBufferOverflow = true, BufferDuration = TimeSpan.FromMilliseconds(600) };
    private readonly NAudio.Wave.SampleProviders.VolumeSampleProvider _volume;
    private readonly object _gate = new();
    private bool _started, _disposed;
    /// <summary>緩衝超過這個量（200 ms）就丟掉舊資料，維持低延遲</summary>
    private const int MaxLatencyBytes = 48_000 * 4 / 5;

    public AudioPlayer() => _volume = new NAudio.Wave.SampleProviders.VolumeSampleProvider(_buffer.ToSampleProvider());

    public float Volume { get => _volume.Volume; set => _volume.Volume = value; }

    private void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            _out = new WasapiOut(AudioClientShareMode.Shared, 60);
            _out.Init(_volume);
            _out.Play();
            Log.Write("音訊輸出已開始");
        }
        catch (Exception e) { Log.Write("開啟音訊輸出失敗 " + e.Message); _out = null; }
    }

    private bool _heardSound;

    public void Push(byte[] pcm)
    {
        if (_disposed) return;
        if (!_heardSound)
            for (int i = 0; i + 1 < pcm.Length; i += 2)
                if (Math.Abs((short)(pcm[i] | pcm[i + 1] << 8)) > 200) { _heardSound = true; Log.Write("收到有聲音的音訊資料"); break; }
        lock (_gate)
        {
            Start();
            if (_buffer.BufferedBytes > MaxLatencyBytes) _buffer.ClearBuffer();   // 跳到最新
            _buffer.AddSamples(pcm, 0, pcm.Length);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _out?.Stop(); _out?.Dispose(); } catch { }
    }
}
