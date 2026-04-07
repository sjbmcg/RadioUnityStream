using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using UnityEngine;
using NLayer;

public class HlsStream : IRadioStream
{
    public event Action<string> OnError;

    public float Volume
    {
        get => _audioSource != null ? _audioSource.volume : _volume;
        set
        {
            _volume = value;
            if (_audioSource != null)
                _audioSource.volume = Mathf.Clamp01(value);
        }
    }

    public bool IsReady => _ringBuffer != null && _ringBuffer.Available >= _minBufferSamples;

    private readonly AudioSource _audioSource;
    private readonly SynchronizationContext _mainThread;
    private volatile SampleRingBuffer _ringBuffer;
    private Thread _streamThread;
    private CancellationTokenSource _cts;
    private float _volume = 1f;
    private volatile int _minBufferSamples;
    private const int RingBufferSeconds = 20;
    private const int ReadyBufferSeconds = 2;
    private const int PlaylistRetryDelayMs = 3000;

    public HlsStream(AudioSource audioSource)
    {
        _audioSource = audioSource;
        _mainThread = SynchronizationContext.Current;
    }

    public void Play(string url)
    {
        Stop();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _streamThread = new Thread(() => StreamLoop(url, token)) { IsBackground = true };
        _streamThread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _streamThread?.Join(500);
        _streamThread = null;
        _ringBuffer = null;
        _cts = null;

        if (_audioSource != null)
            _audioSource.Stop();
    }

    public void OnAudioRead(float[] data)
    {
        int read = _ringBuffer?.Read(data, 0, data.Length) ?? 0;
        for (int i = read; i < data.Length; i++)
            data[i] = 0f;
    }

    public void Dispose() => Stop();

    // ── Stream loop ───────────────────────────────────────────────────────────

    private void StreamLoop(string url, CancellationToken token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible; RadioUnityStream/1.0)");

            string playlistUrl = url;
            var (masterM3u8, masterFinalUrl) = Fetch(client, url, token);

            if (masterM3u8.Contains("#EXT-X-STREAM-INF"))
            {
                var variants = HlsPlaylist.ParseMaster(masterM3u8, masterFinalUrl);
                if (variants.Count == 0) { RaiseError("No variants in master playlist."); return; }
                variants.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
                playlistUrl = variants[0].Url;
            }

            var fmp4Decoder = new FMp4Decoder();
            var tsDecoder   = new TsAudioDecoder();
            byte[] initBytes = null;
            var seenSequences = new HashSet<int>();

            while (!token.IsCancellationRequested)
            {
                string mediaM3u8;
                string mediaFinalUrl;
                try { (mediaM3u8, mediaFinalUrl) = Fetch(client, playlistUrl, token); }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    RaiseError($"Playlist fetch error: {ex.Message}");
                    Thread.Sleep(PlaylistRetryDelayMs);
                    continue;
                }

                var playlist = HlsPlaylist.ParseMedia(mediaM3u8, mediaFinalUrl);

                // fMP4/CMAF: download init segment once to configure decoder + AudioClip.
                if (playlist.InitUrl != null && initBytes == null)
                {
                    initBytes = client.GetByteArrayAsync(playlist.InitUrl).GetAwaiter().GetResult();
                    fmp4Decoder.LoadInitSegment(initBytes);
                    SetupAudioClip(fmp4Decoder.SampleRate, fmp4Decoder.Channels, token);
                }

                foreach (var seg in playlist.Segments)
                {
                    if (token.IsCancellationRequested) return;
                    if (!seenSequences.Add(seg.Sequence)) continue;

                    try { DownloadAndDecodeSegment(client, seg, fmp4Decoder, tsDecoder, token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                            Debug.LogWarning($"[HlsStream] Segment error: {ex.Message}");
                    }
                }

                if (!playlist.IsLive) break;
                WaitOrCancel(token, Math.Max(playlist.TargetDuration, 1) * 1000);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested) RaiseError(ex.Message);
        }
    }

    private void SetupAudioClip(int sampleRate, int channels, CancellationToken token)
    {
        _ringBuffer = new SampleRingBuffer(sampleRate * channels * RingBufferSeconds);
        _minBufferSamples = sampleRate * channels * ReadyBufferSeconds;

        using var clipReady = new ManualResetEventSlim(false);
        _mainThread.Post(_ =>
        {
            var clip = AudioClip.Create("HlsStream", sampleRate * channels, channels, sampleRate, true, OnAudioRead);
            _audioSource.clip = clip;
            _audioSource.loop = true;
            _audioSource.volume = Mathf.Clamp01(_volume);
            _audioSource.Play();
            clipReady.Set();
        }, null);
        clipReady.Wait(token);
    }

    private void DownloadAndDecodeSegment(HttpClient client, HlsSegment seg,
        FMp4Decoder fmp4Decoder, TsAudioDecoder tsDecoder, CancellationToken token)
    {
        byte[] data = client.GetByteArrayAsync(seg.Url).GetAwaiter().GetResult();

        if (fmp4Decoder.Initialised && !seg.Url.Contains(".mp3", StringComparison.OrdinalIgnoreCase))
        {
            // fMP4/CMAF — AudioClip already set up from init segment
            float[] samples = fmp4Decoder.DecodeSegment(data);
            if (samples.Length > 0)
                _ringBuffer?.Write(samples, 0, samples.Length);
        }
        else if (TsAudioDecoder.IsTsData(data))
        {
            // MPEG-TS — decode first; AudioClip set up once we know the actual sample rate
            float[] samples = tsDecoder.DecodeSegment(data);
            if (_ringBuffer == null && tsDecoder.Initialised)
                SetupAudioClip(tsDecoder.SampleRate, tsDecoder.Channels, token);
            if (samples.Length > 0)
                _ringBuffer?.Write(samples, 0, samples.Length);
        }
        else
        {
            // MP3 fallback (explicit .mp3 segments or unknown format)
            if (_ringBuffer == null)
                SetupAudioClip(44100, 2, token);
            DecodeMp3(data);
        }
    }

    private void DecodeMp3(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var mpeg = new MpegFile(new PositionTrackingStream(ms));
        var buf = new float[4096];
        int count;
        while ((count = mpeg.ReadSamples(buf, 0, buf.Length)) > 0)
            _ringBuffer?.Write(buf, 0, count);
    }

    // Returns (content, finalUrl) — finalUrl is the URL after any HTTP redirects,
    // which is the correct base for resolving relative segment/init paths.
    private static (string Content, string FinalUrl) Fetch(HttpClient client, string url, CancellationToken token)
    {
        var response = client.GetAsync(url, token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        string content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;
        return (content, finalUrl);
    }

    private static void WaitOrCancel(CancellationToken token, int ms) =>
        token.WaitHandle.WaitOne(ms);

    private void RaiseError(string message)
    {
        Debug.LogError($"[HlsStream] {message}");
        OnError?.Invoke(message);
    }
}
