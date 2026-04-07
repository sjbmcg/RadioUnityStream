# Unity Internet Radio Streaming

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

> **HLS note:** HE-AAC streams (typically 64k) are not supported, use 128k or higher (AAC-LC). SharpJaad cannot decode HE-AAC.

---

## Prerequisites

- Unity 2021.3 or above
- .NET SDK 8 or above (to restore the NuGet packages and copy the DLLs)
- Active internet connection

---

## Getting the DLLs

The repo includes `UnityPlugins/UnityPlugins.csproj`, a small helper project whose only job is to fetch the right NuGet packages and drop the `netstandard2.0` DLLs into `Assets/Plugins/` automatically. You need the .NET SDK installed first.

### 1. Install the .NET SDK

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

**Other Linux / manual download:** https://dotnet.microsoft.com/download

---

### 2. Run the plugin fetcher

```bash
dotnet build UnityPlugins/UnityPlugins.csproj
```

That's it. `NLayer.dll`, `SharpJaad.dll`, and `SharpJaad.AAC.dll` will appear in `Assets/Plugins/`. Re-run any time you update package versions in the csproj.

> **Why not NuGet inside Unity?** Unity's Mono runtime requires `netstandard2.0` builds. The `net8.0` variants NuGet normally resolves will cause a version conflict at compile time. The helper csproj targets `netstandard2.0` explicitly so the right builds are always selected.

---

## Setup

1. Complete the DLL steps above so `Assets/Plugins/` contains all three DLLs
2. Attach `RadioManager` to a GameObject
3. Add an `AudioSource` component to the same GameObject and assign it in the Inspector
4. Assign the `TMP_Dropdown` and `Slider` UI elements in the Inspector
5. Hit Play - the dropdown populates with all stations automatically

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
