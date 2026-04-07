using System;
using System.Net.Http;
using System.Threading;
using UnityEngine;
using NLayer;

public class IcecastStream : IRadioStream
{
    private const int RingBufferSeconds = 10;
    private const int PreBufferSeconds = 2;

    private readonly AudioSource _audioSource;

    private Thread _streamThread;
    private CancellationTokenSource _cts;
    private SampleRingBuffer _ringBuffer;
    private ManualResetEventSlim _headerReady;

    private string _pendingError;
    private string _initError;
    private int _sampleRate;
    private int _channels;

    public bool IsReady { get; private set; }
    public bool HeadersReady => _headerReady?.IsSet ?? false;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public string InitError => _initError;
    public event Action<string> OnError;

    public IcecastStream(AudioSource audioSource)
    {
        _audioSource = audioSource;
    }

    public float Volume
    {
        get => _audioSource != null ? _audioSource.volume : 0f;
        set { if (_audioSource != null) _audioSource.volume = Mathf.Clamp01(value); }
    }

    // Non-blocking. Poll HeadersReady, then create AudioClip on main thread via RadioManager.
    public void Play(string url)
    {
        Stop();

        IsReady = false;
        _initError = null;
        _pendingError = null;
        _headerReady = new ManualResetEventSlim(false);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _streamThread = new Thread(() =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var client = new HttpClient();
                    client.Timeout = Timeout.InfiniteTimeSpan;

                    var response = client
                        .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                        .GetAwaiter().GetResult();
                    response.EnsureSuccessStatusCode();

                    using var httpStream = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();
                    using var mpegFile = new MpegFile(new PositionTrackingStream(httpStream));

                    if (!_headerReady.IsSet)
                    {
                        _sampleRate = mpegFile.SampleRate;
                        _channels = mpegFile.Channels;
                        _ringBuffer = new SampleRingBuffer(_sampleRate * _channels * RingBufferSeconds);
                        Thread.MemoryBarrier();
                        _headerReady.Set();
                    }

                    int preBufferSamples = _sampleRate * _channels * PreBufferSeconds;
                    var decodeBuffer = new float[_sampleRate / 10 * _channels];

                    while (!token.IsCancellationRequested)
                    {
                        int count = mpegFile.ReadSamples(decodeBuffer, 0, decodeBuffer.Length);
                        if (count == 0) break;
                        _ringBuffer.Write(decodeBuffer, 0, count);

                        if (!IsReady && _ringBuffer.Available >= preBufferSamples)
                            IsReady = true;
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;

                    if (!_headerReady.IsSet)
                    {
                        _initError = ex.Message;
                        _headerReady.Set();
                        break;
                    }

                    Interlocked.Exchange(ref _pendingError, $"Stream dropped, reconnecting: {ex.Message}");
                    Thread.Sleep(2000);
                }
            }
        });

        _streamThread.IsBackground = true;
        _streamThread.Start();
    }

    public void OnAudioRead(float[] data)
    {
        int read = _ringBuffer?.Read(data, 0, data.Length) ?? 0;
        for (int i = read; i < data.Length; i++)
            data[i] = 0f;
    }

    // Call this from MonoBehaviour.Update() to surface errors on the main thread.
    public void FlushErrors()
    {
        string err = Interlocked.Exchange(ref _pendingError, null);
        if (err != null)
            OnError?.Invoke(err);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _streamThread?.Join(500);
        _streamThread = null;
        _ringBuffer = null;
        IsReady = false;

        if (_audioSource != null)
            _audioSource.Stop();
    }

    public void Dispose() => Stop();
}
