using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Coordinates UI and radio stream lifecycle. Supports both Icecast (MP3) and HLS (BBC, NPR, etc.).
/// URL ending in .m3u8 → HlsStream. Everything else → IcecastStream.
/// </summary>
public class RadioManager : MonoBehaviour
{
    public Slider volumeSlider;
    public TMP_Dropdown radioDropdown;
    public AudioSource audioSource;

    private readonly string[] _stationNames = {
        "DEFCON Radio",
        "Groove Salad",
        "Drone Zone",
        "Indie Pop Rocks!",
        "BBC Radio 1",
        "BBC Radio 2",
        "BBC Radio 3",
        "BBC Radio 4",
        "BBC Radio 5 Live",
        "BBC Radio 6 Music",
        "BBC World Service"
    };

    private readonly string[] _urls = {
        // Icecast (SomaFM)
        "http://ice3.somafm.com/defcon-128-mp3",
        "http://ice3.somafm.com/groovesalad-128-mp3",
        "http://ice3.somafm.com/dronezone-128-mp3",
        "http://ice3.somafm.com/indiepop-128-mp3",
        // BBC Radio (HLS via lsn.lv proxy — BBC changes direct URLs frequently)
        "https://lsn.lv/bbcradio.m3u8?station=bbc_radio_one&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_radio_two&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_radio_three&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_radio_fourfm&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_radio_five_live&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_6music&bitrate=320000",
        "https://lsn.lv/bbcradio.m3u8?station=bbc_world_service&bitrate=320000"
    };

    private IRadioStream _stream;

    void Start()
    {
        if (volumeSlider != null)
        {
            volumeSlider.value = 1f;
            volumeSlider.onValueChanged.AddListener(v => { if (_stream != null) _stream.Volume = v; });
        }

        if (radioDropdown != null)
        {
            radioDropdown.ClearOptions();
            radioDropdown.AddOptions(new System.Collections.Generic.List<string>(_stationNames));
            radioDropdown.onValueChanged.AddListener(ChangeStation);
            radioDropdown.value = 0;
        }

        StartCoroutine(PlayStream(_urls[0]));
    }

    void Update()
    {
        // IcecastStream surfaces background-thread errors here on the main thread
        (_stream as IcecastStream)?.FlushErrors();
    }

    void OnDestroy()
    {
        _stream?.Stop();
        _stream?.Dispose();
    }

    private IEnumerator PlayStream(string url)
    {
        _stream?.Stop();
        _stream?.Dispose();
        _stream = null;

        if (url.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            yield return StartCoroutine(PlayHls(url));
        }
        else
        {
            yield return StartCoroutine(PlayIcecast(url));
        }
    }

    private IEnumerator PlayIcecast(string url)
    {
        var stream = new IcecastStream(audioSource);
        stream.OnError += e => Debug.LogError($"[Radio] {e}");
        _stream = stream;

        stream.Play(url);

        yield return new WaitUntil(() => stream.HeadersReady);

        if (stream.InitError != null)
        {
            Debug.LogError($"[Radio] Failed to connect: {stream.InitError}");
            yield break;
        }

        // AudioClip must be created on the main thread
        var clip = AudioClip.Create(
            "IcecastStream",
            stream.SampleRate * stream.Channels,
            stream.Channels,
            stream.SampleRate,
            true,
            stream.OnAudioRead);

        audioSource.clip = clip;
        audioSource.loop = true;

        yield return new WaitUntil(() => stream.IsReady);
        audioSource.Play();
    }

    private IEnumerator PlayHls(string url)
    {
        var stream = new HlsStream(audioSource, this);
        stream.OnError += e => Debug.LogError($"[Radio] {e}");
        _stream = stream;

        stream.Play(url); // non-blocking; creates AudioClip internally with fixed 44100/stereo
        yield return new WaitUntil(() => stream.IsReady);
    }

    public void ChangeStation(int index)
    {
        if (index < 0 || index >= _urls.Length)
        {
            Debug.LogError($"[Radio] Invalid station index: {index}");
            return;
        }
        StartCoroutine(PlayStream(_urls[index]));
    }
}
