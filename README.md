# Unity Internet Radio Streaming

[![Build](https://github.com/sjbmcg/RadioUnityStream/actions/workflows/build.yml/badge.svg)](https://github.com/sjbmcg/RadioUnityStream/actions/workflows/build.yml)

Streams live internet radio inside Unity. Works on Windows, macOS, and Linux with no OS-level audio codecs required. Built entirely on pure C# libraries with no native dependencies.

Supports **Icecast/SHOUTcast** streams (SomaFM, NPR etc.) and **HLS** streams (SomaFM, and more) out of the box.

Tested on Windows and WSL2. Not entirely sure about native macOS or Linux Unity builds yet, but nothing in the stack should be platform-specific.

---

## How it works

| Component | Role |
|---|---|
| **NLayer** | Pure C# MP3 decoder, handles Icecast streams and MP3 HLS segments |
| **SharpJaad** | Pure C# AAC/MP4 decoder, handles fMP4 (CMAF) and MPEG-TS HLS segments |
| **SampleRingBuffer** | Thread-safe float ring buffer that bridges the background download thread to Unity's audio callback |
| **HlsPlaylist** | M3U8 parser, handles master playlists, variant selection, and init segments (`#EXT-X-MAP`) |
| **FMp4Decoder** | Custom fMP4 box parser + SharpJaad integration, extracts AAC frames from `moof`/`mdat` fragments |
| **TsAudioDecoder** | Pure C# MPEG-TS parser, discovers audio PID via PAT/PMT, strips PES headers, decodes ADTS frames |
| **StreamTester** | Standalone .NET console app for validating the pipeline without needing Unity open |

---

## Supported Stations

| Station | Type |
|---|---|
| SomaFM - DEFCON Radio, Groove Salad, Drone Zone, Indie Pop Rocks! | Icecast (MP3) |
| KUT News (NPR Austin) | Icecast (MP3) |
| KUTX Music (NPR Austin) | Icecast (MP3) |
| SomaFM - Groove Salad 128k | HLS fMP4/AAC |

Adding more stations: add the URL and name to the `_urls` and `_stationNames` arrays in `RadioManager.cs`. URLs ending in `.m3u8` are routed to the HLS path automatically; everything else goes through the Icecast path.

For a large collection of stream URLs to try, check out [mikepierce/internet-radio-streams](https://github.com/mikepierce/internet-radio-streams).

> **HLS note:** HE-AAC streams (typically 64k) are not supported, use 128k or higher (AAC-LC). SharpJaad cannot decode HE-AAC.

---

## Getting started

### 1. Install the .NET SDK

You need this to fetch the audio decoding libraries. Skip if you already have it.

**Windows**
```powershell
winget install Microsoft.DotNet.SDK.8
```

**macOS**
```bash
brew install dotnet
```

**Ubuntu / Debian**
```bash
sudo apt update && sudo apt install -y dotnet-sdk-8.0
```

**Other:** https://dotnet.microsoft.com/download

---

### 2. Fetch the plugins

From the repo root:

```bash
dotnet build UnityPlugins/UnityPlugins.csproj
```

This creates `Assets/Plugins/` and drops the three required DLLs in automatically. You only need to run this once after cloning.

---

### 3. Open in Unity

Open the project in Unity 2021.3 or above. Unity will detect the new DLLs and import them - you'll see them appear in `Assets/Plugins/` in the Project window.

---

### 4. Set up the scene

1. Attach `RadioManager` to a GameObject
2. Add an `AudioSource` to the same GameObject and assign it in the Inspector
3. Assign a `TMP_Dropdown` and `Slider` in the Inspector
4. Hit Play - the dropdown populates with all stations automatically

---

## Testing without Unity (StreamTester)

A standalone console app lives in `StreamTester/` for validating the pipeline:

```bash
# Icecast / MP3
cd StreamTester && dotnet run -- http://ice3.somafm.com/groovesalad-128-mp3

# HLS / fMP4 / AAC
cd StreamTester && dotnet run -- --hls https://hls.somafm.com/hls/groovesalad/128k/program.m3u8
```

---

## Legal

For personal, non-commercial use only. Comply with your local copyright laws and the terms of service of each radio station.

- NLayer - MIT licence
- SharpJaad - MIT licence
