# Marquee

A Windows media player in the spirit of Infuse: Plex and local libraries, with playback that gets
HDR10, HDR10+, Dolby Vision and Atmos right.

The plan is two playback engines chosen per file:

- **libmpv** (default): every codec and container, TrueHD/DTS-HD MA, PGS/ASS subtitles, Dolby
  Vision applied by libplacebo and output as HDR10.
- **Media Foundation**: the Dolby-licensed paths Windows already ships — real Dolby Vision
  signalling via the Dolby Vision Extension, and DD+ Atmos objects via the system decoder into
  Windows Spatial Sound. Marquee renders its subtitles itself (SRT, ASS/SSA, WebVTT through libass;
  PGS and VobSub through FFmpeg), so subtitles don't cost real Dolby Vision.

## The app

`src/Marquee` is the player itself: a native WinUI 3 app (Mica, the WinUI title bar and navigation,
Segoe Fluent icons) with a dark graphite and amber look.

- **Library:** Plex (PIN sign-in, every server and library you can reach) and folders on this PC,
  merged into one SQLite library on this PC, so browsing is instant. Plex syncs at startup and every
  30 minutes; watched folders rescan when files change.
- **Local files** are matched with TMDB, like Infuse: posters, backdrops, logos, cast, ratings and
  episode titles. TMDB needs a free key from themoviedb.org (Settings → Library); artwork saved next
  to the files wins. A wrong match can be fixed with "Edit match…".
- **Screens:** Home (continue watching, recently added, up next), Movies and TV grids, movie and
  series detail pages, Sources, Settings.
- **Player:** picks the engine per file. Media Foundation when it adds something mpv can't (real
  Dolby Vision, Atmos objects, DTS:X) in MKV, MP4 or MPEG-TS/M2TS, mpv otherwise or for subtitles
  it can't show. Plex items play the original file directly over HTTP. Progress and watched state sync back to Plex. It also does
  resume, skip intro/credits, next-episode countdown, chapters, audio and subtitle choice, picture
  in picture, frame stepping and lip-sync.

```powershell
mise install   # the .NET 10 SDK and the native build tools, pinned in mise.toml
./tools/fetch-libmpv.ps1; ./tools/fetch-ffmpeg.ps1; ./tools/build-native.ps1; ./tools/build-libass.ps1
dotnet build src/Marquee -p:Platform=x64
src/Marquee/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/Marquee.exe
```

### Installing

`tools/install.ps1` publishes a self-contained Release, signs it and installs it for the current
user: `%LOCALAPPDATA%\Programs\Marquee`, a Start menu shortcut and an entry in Settings → Apps that
uninstalls it. No administrator rights; run it again to update. The library and settings live in
`%LOCALAPPDATA%\Marquee` and survive updates and uninstalls.

Signing is optional. `tools/new-signing-cert.ps1` creates a self-signed code-signing certificate
once: the private key stays in your user store and can't be exported, and the public certificate is
trusted on this PC (one UAC prompt), so Windows shows the signature as valid and elevation prompts
show a verified publisher. Without it, the install is unsigned.

```powershell
./tools/new-signing-cert.ps1   # once
./tools/install.ps1
```

## Engine spike

`spike/EngineSpike` is a throwaway WinUI 3 harness that plays a file through either engine in the
same window and reports what each one negotiated.

```powershell
dotnet build spike/EngineSpike -p:Platform=x64
spike/EngineSpike/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/EngineSpike.exe
```

Command line: `EngineSpike.exe [--engine mpv|mf] [--start SECONDS] [--probe] [--mute]
[--fullscreen] [--exit-after N] [--native-mkv] [--no-dv] [--audio-track N] [path]`. With
`--exit-after`, a heartbeat logs the playback position every 3 s.

| Key | Action |
|---|---|
| F2 | Swap engine: same file, same position, same audio language |
| F3 | Next audio track in the current engine |
| F11 / Esc | Full screen on / off (mpv's own `f` also works) |

The test-file drop-down reads `spike/EngineSpike/samples.txt`, one `label|path` per line. It's
gitignored; copy `samples.example.txt` and point it at your own files. Picking a file plays it in
whichever engine is active. **Probe Atmos decoding** asks Windows' Dolby decoder whether each audio
track really carries Atmos objects (see below). The mpv engine loads `%APPDATA%\mpv\mpv.conf`.
Everything in the status pane is also written to `%LOCALAPPDATA%\Marquee\EngineSpike\spike.log`,
with mpv's own log next to it.

### Findings (2026-11-02)

| | mpv | Media Foundation |
|---|---|---|
| DV P5 MKV (WEB-DL) | RPU applied → HDR10 PQ @ 1000 nits | **Fails**: `0xC00D3E8C` "format recognized but invalid" |
| DV P8 MKV (WEB-DL) | RPU applied | Plays as plain HEVC: no DV codec string, no HDR color attributes |
| DV P8 MP4 (WEB-DL) | RPU applied | Plays; exposes `dvhe.08`, the DV config record, PQ + BT.2020 |
| DV P7 FEL MKV (UHD remux) | RPU applied to base layer | not tested |
| DD+ Atmos | Decodes the 5.1 bed → 7.1 out, objects lost | Windows' Dolby decoder emits objects (probe below) |

**Real Dolby Vision is confirmed**: with Dolby Vision enabled on the monitor, the P8 MP4 through
Media Foundation switches it into Dolby Vision mode. The same file through mpv, or the P8 MKV
through Media Foundation, stays HDR10.

Windows' MKV reader is what drops Dolby Vision. MP4 keeps it.

### Marquee's MKV reader

`spike/EngineSpike/Matroska` is Marquee's own Matroska demuxer, which feeds Media Foundation through a
`MediaStreamSource`. Real Dolby Vision depends on two attributes the MP4 reader sets and the MKV
reader doesn't (both in `mfapi.h`):

- `MF_MT_VIDEO_RENDERER_EXTENSION_PROFILE = "dvhe.05"` / `"dvhe.08"` loads the Dolby Vision
  Extensions' video renderer effect. Its manifest registers only `dvhe.05`, `dvhe.08` and `dvav.09`,
  so profile 7 has to be converted first (see below).
- `MF_MT_FORWARD_CUSTOM_NALU = 62` makes the HEVC decoder pass the RPU (NAL unit type 62) downstream.

The profile comes from the track's `dvcC`/`dvvC` block addition mapping. Video is converted from
length-prefixed NAL units to Annex B, with parameter sets before keyframes. HDR color and mastering
metadata come from the Matroska `Colour` element. DD+/AC-3/AAC are passed through compressed so
Windows' Dolby decoder does the decoding, Atmos objects included. The reader also handles lacing,
header stripping, zlib, cues-based seeking and audio track switching. The "Use Windows' MKV reader"
checkbox (or `--native-mkv`) switches back for comparison.

Windows itself has no TrueHD or DTS decoder:

- **DTS:** DTS Sound Unbound (a paid Store app) installs `DTSXDecoder`, a Media Foundation decoder
  for DTS, DTS-HD MA and DTS:X with spatial-object output. It only fails to load in Marquee
  because Marquee is unpackaged and can't see the VC++ runtime package the decoder links against;
  preloading that runtime fixes it. Marquee passes DTS through to it, falling back to FFmpeg when
  Sound Unbound isn't installed.
- **TrueHD:** no decoder is available to Windows apps at all. Marquee decodes it in-process with
  FFmpeg's libavcodec and hands Media Foundation float PCM in the real channel layout. That's the
  lossless 7.1 bed, the same as mpv. For the Atmos objects, see below.

| MKV through Media Foundation | Windows' reader | Marquee's reader |
|---|---|---|
| DV P5 (WEB-DL) | fails: `0xC00D3E8C` | **real Dolby Vision** |
| DV P8 (WEB-DL) | plain HEVC, no HDR metadata | **real Dolby Vision**, seeks |
| DV P8 + TrueHD + DTS-HD MA (UHD remux) | — | `dvhe.08` requested; TrueHD 7.1 and DTS-HD MA 7.1 decoded, real time, 0 errors |
| DV P7 MEL and FEL (UHD remuxes) | — | **real Dolby Vision** via per-frame 7 → 8.1 RPU conversion |

### MPEG-TS and MP4 subtitles

The same `MediaStreamSource` also takes MPEG-TS and Blu-ray M2TS, demuxed by FFmpeg's libavformat
instead of Marquee's MKV reader. Tracks come out described as Matroska would store them, so
everything above applies:

- Dolby Vision from the stream's DOVI descriptor, including the 7 → 8.1 conversion.
- HDR10 metadata from the bitstream.
- TrueHD Atmos objects.
- PGS subtitles, collected from one PES per segment into display sets.

Blu-ray discs that carry the Dolby Vision enhancement layer on a PID of its own play as HDR10.

MP4 stays with Windows' own source, which keeps Dolby Vision but doesn't hand over subtitle tracks.
Marquee reads the chosen tx3g track through libavformat on its own, around the playing time. The
text and its italics go through the same libass path as SRT.

| Through Media Foundation | Result |
|---|---|
| UHD M2TS, HDR10 + TrueHD Atmos + PGS | HDR10 metadata, objects about 1 ms in sync, PGS shown |
| DV P8 MP4 WEB-DL with tx3g subtitles | real Dolby Vision path with subtitles on |
| DV P8 and P7 clips remuxed to MPEG-TS | Dolby Vision plugin loads; P7 RPUs convert to 8.1 |

### Dolby Vision profile 7

UHD Blu-ray uses profile 7 (HDR10 base layer + enhancement layer + RPU), which Windows' Dolby Vision
Extensions don't handle.

- The MKV reader converts each frame's RPU to profile 8.1 with dovi_tool's `dolby_vision` crate
  (mode 2, via `native/dovi-ffi`), drops the enhancement layer, and requests `dvhe.08`.
- MEL discs lose nothing. FEL discs lose the enhancement layer's extra precision, as on any player
  without FEL support.
- `--dv-info` tells them apart. All 17 profile 7 remuxes tested so far convert, 10 MEL and 7 FEL.
- Combined with TrueHD Atmos objects, a UHD remux plays with real Dolby Vision and Atmos heights.

### Telling whether a track really has Atmos objects

Listening isn't reliable: different tracks have different mixes and loudness, and louder reads as
"better". The probe asks Windows' **Dolby Digital Plus Decoder MFT** directly. It offers a
`MFAudioFormat_Float_SpatialObjects` output (up to 16 dynamic objects) for any DD+ stream, but once it
has read real audio:

- **JOC (Atmos) stream**: it keeps object output and tries to write objects. Only the audio
  renderer can allocate the `IMFSpatialAudioSample` it needs, so the probe sees `E_NOINTERFACE`.
- **Plain DD+ stream**: it raises a format change and withdraws the object output.

| Track | Tagged as | Probe |
|---|---|---|
| WEB-DL MP4, original language | DD+ Atmos | ✔ objects |
| Same file, dubbed track | DD+ 5.1 | ✘ no objects |
| WEB-DL MKV, both languages | DD+ Atmos | ✔ objects / ✔ objects |

The MP4 `dec3` box's JOC flag was clear even on the Atmos track, so container signalling can't be
trusted; the decoder test is the one that counts. ffmpeg (and so mpv) has no JOC decoder at all.

### TrueHD Atmos objects

Every Windows player, mpv included, decodes only TrueHD's 7.1 layer and folds the Atmos objects
into it. Marquee decodes the object layer (presentation 3) live with the `truehd` crate, a
research decoder from [truehdd](https://github.com/truehdd/truehdd), via `native/truehd-ffi`. It
renders each object at its position through Windows Spatial Sound, so Dolby Atmos for Headphones
gets real heights.

- In this mode Media Foundation plays video only, and the audio follows its clock.
- Enable it with the "TrueHD as Atmos objects" checkbox or `--truehd-atmos`.
- `[` / `]` shift lip-sync by 10 ms.

| UHD remux | Elements | Sync (muted test) |
|---|---|---|
| DV P8, TrueHD Atmos | LFE + 11 objects (two overhead) | ±1 ms after startup |
| DV P7 FEL, TrueHD Atmos | LFE + 11 objects (four sweeping floor ↔ ceiling) | ±0.1 ms after startup |

truehdd decodes the object layer at about 49× real time. On headphones the difference from the 7.1
downmix is real but subtle, mostly height and vertical movement. The Atmos lab (`--atmos-lab`)
A/Bs the two sample-locked and has a position test.

## License

Marquee is free software under the [GNU General Public License v3.0](LICENSE). Builds bundle
third-party components under their own licenses, including a GPL build of libmpv and LGPL FFmpeg;
see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
