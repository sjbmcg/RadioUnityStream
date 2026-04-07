using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using UnityEngine;
using UnityEngine.Networking;
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
    private readonly MonoBehaviour _runner;
    private SampleRingBuffer _ringBuffer;
    private Thread _streamThread;
    private CancellationTokenSource _cts;
    private float _volume = 1f;
    private int _minBufferSamples;
    private const int SampleRate = 44100;
    private const int Channels = 2;
    private const int RingBufferSeconds = 20;
    private const int ReadyBufferSeconds = 2;

    public HlsStream(AudioSource audioSource, MonoBehaviour coroutineRunner)
    {
        _audioSource = audioSource;
        _runner = coroutineRunner;
    }

    public void Play(string url)
    {
        Stop();

        _cts = new CancellationTokenSource();
        _ringBuffer = new SampleRingBuffer(SampleRate * Channels * RingBufferSeconds);
        _minBufferSamples = SampleRate * Channels * ReadyBufferSeconds;

        var clip = AudioClip.Create("HlsStream", SampleRate * Channels, Channels, SampleRate, true, OnAudioRead);
        _audioSource.clip = clip;
        _audioSource.loop = true;
        _audioSource.volume = Mathf.Clamp01(_volume);
        _audioSource.Play();

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

    // -------------------------------------------------------------------------

    private void StreamLoop(string url, CancellationToken token)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

            string playlistUrl = url;
            string masterM3u8 = Fetch(client, url, token);

            if (masterM3u8.Contains("#EXT-X-STREAM-INF"))
            {
                var variants = HlsPlaylist.ParseMaster(masterM3u8, url);
                if (variants.Count == 0) { RaiseError("No variants in master playlist"); return; }
                variants.Sort((a, b) => b.Bandwidth.CompareTo(a.Bandwidth));
                playlistUrl = variants[0].Url;
            }

            var seenSequences = new HashSet<int>();

            while (!token.IsCancellationRequested)
            {
                string mediaM3u8;
                try { mediaM3u8 = Fetch(client, playlistUrl, token); }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;
                    RaiseError($"Playlist fetch error: {ex.Message}");
                    Thread.Sleep(3000);
                    continue;
                }

                var playlist = HlsPlaylist.ParseMedia(mediaM3u8, playlistUrl);

                foreach (var seg in playlist.Segments)
                {
                    if (token.IsCancellationRequested) return;
                    if (!seenSequences.Add(seg.Sequence)) continue;

                    try { DownloadAndDecodeSegment(client, seg, token); }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        if (!token.IsCancellationRequested)
                            Debug.LogWarning($"[HlsStream] Segment error: {ex.Message}");
                    }
                }

                if (!playlist.IsLive) break;

                int pollMs = Math.Max(playlist.TargetDuration, 1) * 1000;
                WaitOrCancel(token, pollMs);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                RaiseError(ex.Message);
        }
    }

    private void DownloadAndDecodeSegment(HttpClient client, HlsSegment seg, CancellationToken token)
    {
        bool isAac = seg.Url.Contains(".aac", StringComparison.OrdinalIgnoreCase) ||
                     seg.Url.Contains(".ts", StringComparison.OrdinalIgnoreCase);

        if (isAac)
        {
            DecodeAacViaCoroutine(seg.Url, token);
        }
        else
        {
            byte[] data = client.GetByteArrayAsync(seg.Url).GetAwaiter().GetResult();
            DecodeMp3(data);
        }
    }

    private void DecodeMp3(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var tracked = new PositionTrackingStream(ms);
        using var mpeg = new MpegFile(tracked);

        var buf = new float[4096];
        int count;
        while ((count = mpeg.ReadSamples(buf, 0, buf.Length)) > 0)
            _ringBuffer?.Write(buf, 0, count);
    }

    private void DecodeAacViaCoroutine(string url, CancellationToken token)
    {
        float[] samples = null;
        string error = null;
        var done = new ManualResetEventSlim(false);

        _runner.StartCoroutine(FetchAudioClip(url, result => samples = result, err => error = err, done));

        done.Wait(token);

        if (error != null)
            throw new Exception(error);

        if (samples != null && samples.Length > 0)
            _ringBuffer?.Write(samples, 0, samples.Length);
    }

    private static IEnumerator FetchAudioClip(string url, Action<float[]> onSamples, Action<string> onError, ManualResetEventSlim done)
    {
        using var req = UnityWebRequestMultimedia.GetAudioClip(url, AudioType.UNKNOWN);
        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            onError(req.error);
            done.Set();
            yield break;
        }

        var clip = DownloadHandlerAudioClip.GetContent(req);
        if (clip == null)
        {
            onError("Null AudioClip from UnityWebRequest");
            done.Set();
            yield break;
        }

        var samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);
        onSamples(samples);
        done.Set();
    }

    private static string Fetch(HttpClient client, string url, CancellationToken token)
    {
        var response = client.GetAsync(url, token).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    private static void WaitOrCancel(CancellationToken token, int ms)
    {
        token.WaitHandle.WaitOne(ms);
    }

    private void RaiseError(string message)
    {
        Debug.LogError($"[HlsStream] {message}");
        OnError?.Invoke(message);
    }
}
