# Unity Internet Radio Streaming

## Overview

Streams live internet radio stations inside Unity. Started as a Windows-only implementation using NAudio — this branch rewrites the audio pipeline to work cross-platform using pure C# with no OS codec dependencies.

Supports both **Icecast/SHOUTcast** streams (SomaFM etc.) and **HLS** streams (BBC Radio, NPR etc.) out of the box.

---

## What changed from main

The original code used NAudio's `MediaFoundationReader` which relies on Windows Media Foundation — meaning it only worked on Windows. This branch replaces that with:

- **NLayer** — pure C# MP3 decoder, no native dependencies
- **Custom ring buffer** — bridges the background decode thread to Unity's audio callback
- **HLS client** — M3U8 playlist parser + segment downloader for BBC and other major broadcasters
- **StreamTester** — standalone .NET console app to validate the streaming pipeline without needing Unity (tested on Windows and Linux/WSL2)

Hasn't been tested directly on macOS or Linux Unity builds yet, but the core decoding pipeline was confirmed working on Linux via WSL2.

---

## Supported Stations

| Station | Type |
|---|---|
| SomaFM (DEFCON, Groove Salad, Drone Zone, Indie Pop Rocks!) | Icecast |
| BBC Radio 1, 2, 3, 4, 5 Live, 6 Music, World Service | HLS |

Adding more stations: drop the URL into the `_urls` and `_stationNames` arrays in `RadioManager.cs`. URLs ending in `.m3u8` are automatically handled as HLS, everything else as Icecast.

---

## Prerequisites

- Unity 2021.3 or above
- NLayer.dll in `Assets/Plugins/` (copy from `StreamTester/bin/Release/net8.0/NLayer.dll`)
- Active internet connection

## Setup

1. Copy `StreamTester/bin/Release/net8.0/NLayer.dll` into `Assets/Plugins/`
2. Attach `RadioManager` to a GameObject
3. Add an `AudioSource` component to the same GameObject and assign it in the Inspector
4. Assign the `TMP_Dropdown` and `Slider` UI elements in the Inspector
5. Hit Play — the dropdown populates automatically

---

## Legal Disclaimer

For personal, non-commercial use only. Comply with your local copyright laws and the terms of service of individual radio stations. NLayer is MIT licensed. The creators assume no liability for misuse of this software.
