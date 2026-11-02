# Marquee

A Windows media player in the spirit of Infuse: a polished library for Plex and local files, with
playback that gets HDR10, HDR10+, Dolby Vision and Dolby Atmos right. Personal project, Windows only.

## What it is

- A **client**: it browses a Plex server and local or network folders, plays media, and syncs watch
  progress back to Plex.
- **Two playback engines, chosen per file:**
  - **libmpv**, the default: plays everything. Every codec and container FFmpeg handles,
    TrueHD/DTS-HD MA, PGS/ASS subtitles. Dolby Vision is applied by libplacebo/libdovi and output as
    HDR10; HDR10+ dynamic metadata feeds libplacebo's tone mapping.
  - **Media Foundation**, for the Dolby-licensed paths only Windows has: real Dolby Vision signalling
    to the display (Dolby Vision Extensions) and DD+ Atmos objects into Windows Spatial Sound
    (Windows' Dolby decoder).
- That combination doesn't exist in any other Windows player. Every Plex-capable client is
  mpv-only, so none of them signals real Dolby Vision or delivers Atmos objects. That gap is why
  this project exists.

## What it isn't

- Not a media server, transcoder or downloader. Plex serves and Sonarr/Radarr acquire; Marquee plays.
- Not cross-platform. It relies on Windows-only pieces on purpose: Media Foundation, Store codec
  extensions, Spatial Sound.
- Not a new decoder or renderer. Decoding and rendering come from libmpv and Media Foundation;
  Marquee's job is the glue, the library and the experience. The one exception is its own MKV
  demuxer (below).
- It never ships Dolby or DTS code. Licensed decoding uses what Windows already has installed.
- No DRM streaming services.

## What it's aiming to be

Infuse-level quality on Windows:

- A beautiful library: artwork, metadata, TMDB matching for local files, continue watching.
- Plex: PIN sign-in, libraries, direct play of the original file (no transcoding when avoidable),
  progress and scrobbling, intro and credits markers.
- Automatic engine choice per file. The user shouldn't need to know there are two engines.
- Display handling: switch HDR on and off, and match the refresh rate to the content.
- Track preferences for multilingual libraries: dual audio, preferred audio and subtitle
  languages, forced subtitles.
- Parked for later: volume stabilization / night mode (mpv audio filters; range compression in the
  Dolby decoder for Media Foundation).

## Status (2026-11-02)

Engine-spike stage. `spike/EngineSpike` is a throwaway WinUI 3 harness that plays a file through
either engine in the same window and logs what each one negotiated. There is no library UI or Plex
code yet. Proven so far (details and tables in README.md):

- mpv embedded in a WinUI window with its own HDR swap chain.
- Real Dolby Vision through Media Foundation for MP4 **and MKV** (profiles 5 and 8), confirmed on
  the monitor.
- Windows' Dolby decoder emits Atmos objects for JOC tracks.
- TrueHD plays in the Media Foundation engine, decoded in-process with FFmpeg. DTS, DTS-HD MA and
  DTS:X play through DTS Sound Unbound's Windows decoder, with FFmpeg as the fallback.
- **TrueHD Atmos objects play live** (`--truehd-atmos`), which no other Windows player does:
  - TrueHD's object layer is decoded with the `truehd` crate.
  - The objects are rendered as positioned objects through Windows Spatial Sound.
  - Audio is locked to Media Foundation's video clock.
  - Verified on two UHD remuxes: about 1 ms sync, confirmed by ear.
- **Dolby Vision profile 7 (UHD Blu-ray) plays as real Dolby Vision:**
  - Each frame's RPU is converted to 8.1 (dovi_tool mode 2) and the enhancement layer dropped.
  - Tested on 17 P7 remuxes; all convert: 10 MEL (lossless) and 7 FEL (EL precision dropped).
  - Confirmed on a Dolby Vision display, with a correct picture, for both MEL and FEL.

Next, in order:

1. HTTP reading for the MKV reader (Plex direct-play URLs, with range requests).
2. Subtitles for the Media Foundation engine (XAML overlay; SRT, ASS, PGS).
3. Engine routing rules.
4. Then the real app: library, Plex and an Infuse-style UI on top of both engines.

## Tech stack

- C# on .NET 9 (the only SDK installed; it goes out of support in November 2026, so the real app
  should target .NET 10 LTS). WinUI 3 via Windows App SDK 2.6.4, unpackaged
  (`WindowsPackageType=None`), self-contained, x64 only.
- **libmpv**: `libmpv-2.dll` from shinchiro's `mpv-dev-x86_64-v3` builds. `tools/fetch-libmpv.ps1`
  downloads it into `lib/` (gitignored), and the csproj copies it to the output. P/Invoke lives in
  `Mpv/LibMpv.cs`.
- mpv is embedded with `wid` into a plain Win32 child window (`Interop/VideoHostWindow.cs`), so it
  keeps its own D3D11 swap chain (gpu-next + libplacebo + libdovi) and can output HDR. The cost is
  airspace: XAML can't draw over that window, so popups and overlays must hide it or live elsewhere.
  mpv's own OSD and OSC work. The spike loads the user's `%APPDATA%\mpv\mpv.conf` (`config=yes`).
- **Media Foundation**: WinRT `MediaPlayer` plus WinUI `MediaPlayerElement`. It depends on Store
  packages: HEVC Video Extensions, Dolby Vision Extensions (`DolbyLaboratories.DolbyVisionAccess`)
  and the Dolby Digital Plus decoder (`DolbyLaboratories.DolbyDigitalPlusDecoderOEM`). All of them
  work from the unpackaged app.
- **Marquee's MKV reader** (`Matroska/`). Windows' own MKV source drops Dolby Vision and HDR
  attributes, and fails outright on DV profile 5. So Marquee demuxes Matroska itself and feeds
  Media Foundation through a `MediaStreamSource`:
  - The Dolby Vision trigger is two attributes:
    - `MF_MT_VIDEO_RENDERER_EXTENSION_PROFILE` = `"dvhe.05"` or `"dvhe.08"` loads the Dolby Vision
      Extensions' video renderer effect. Its manifest registers only `dvhe.05`, `dvhe.08` and
      `dvav.09`.
    - `MF_MT_FORWARD_CUSTOM_NALU = 62` forwards the RPU.
  - Video: length-prefixed NAL units become Annex B, with parameter sets before keyframes. HDR
    metadata comes from the Matroska `Colour` element.
  - **Profile 7 → 8.1:** `NalConverter` rewrites every RPU NAL unit (type 62) through
    `native/dovi-ffi` and drops enhancement-layer NAL units (type 63). The stream is then requested
    as `dvhe.08`.
    - `native/dovi-ffi` is a Rust cdylib (`marquee_dovi.dll`) over dovi_tool's `dolby_vision`
      crate 3.4.0 (MIT), using `ConversionMode::To81` (mode 2: luma/chroma mapping set to no-op).
    - The P7 base layer is already HDR10 (`BL compat 6`), so mapping is only needed to combine the
      layers.
    - MEL loses nothing. FEL loses the enhancement layer's residual precision, as on any player
      without FEL support.
    - `--dv-info out.txt <mkv>` reports a file's first RPU, e.g. `7 FEL`.
  - Audio: DD+, AC-3 and AAC pass through compressed, so Windows' Dolby decoder decodes them with
    objects intact.
  - **DTS** goes to DTS's own decoder when the user has **DTS Sound Unbound**. Its package
    registers `DTSXDecoder`, a Media Foundation audio decoder for DTS, DTS-HD and DTS:X whose
    outputs include `MFAudioFormat_Float_SpatialObjects`, so DTS:X objects can reach Windows
    Spatial Sound.
    - It links against the `*_app` C++ runtime in the `Microsoft.VCLibs.140.00` framework
      package. An unpackaged process can't resolve those DLLs, so activation fails with
      `0x8007007E`.
    - `Interop/StoreCodecRuntime.cs` preloads the four runtime DLLs by full path at startup,
      after which the playback pipeline picks `DTSXDecoder` by itself (confirmed by
      `DTSXDecoder.dll` loading mid-playback).
    - The MKV reader passes DTS through as `MFAudioFormat_DTS_HD`, splitting laced frames into
      one sample each.
    - Without Sound Unbound, or with `--ffmpeg-dts`, DTS falls back to FFmpeg.
  - **TrueHD** has no Windows decoder at all. It's decoded in-process by FFmpeg's libavcodec
    (`Matroska/FfmpegAudioDecoder.cs`) and handed to Media Foundation as float PCM in the real
    channel layout. That gives the lossless 7.1 bed, but no Atmos objects (the same as mpv).
    - Bindings: FFmpeg.AutoGen 9.0.1.1.
    - Libraries: BtbN's LGPL shared 9.0 build (`avcodec-63`, `avutil-61`, `swresample-7`),
      fetched by `tools/fetch-ffmpeg.ps1` into `lib/ffmpeg/`.
    - The package version must match the FFmpeg branch; check `ffmpeg.LibraryVersionMap`.
    - These libraries are separate from the FFmpeg statically linked inside libmpv.
- **TrueHD Atmos objects** (`SpatialAudio/`, `native/truehd-ffi/`):
  - **Decoder:** `native/truehd-ffi` is a Rust cdylib (`marquee_truehd.dll`) over the `truehd`
    crate 0.7.2 (Apache-2.0, a reverse-engineered research decoder from github.com/truehdd/truehdd).
    - It decodes presentation 3 one Matroska packet at a time and returns element-major float PCM
      plus metadata events.
    - Channels `0..beds` are bed channels, labelled `L R C LFE Ls Rs Lb Rb Tfl…`; the rest are
      objects.
    - Event time is the access unit's start + `sample_offset` + 32·`block_offset_factor` +
      `evo_sample_offset`. Positions come from `get_damf_pos()`, gain is in whole dB, ramp is in
      samples.
    - Restatements equal to the current target are dropped, so they don't cut ramps short.
    - Every export is wrapped in `catch_unwind`, because corrupt input can panic the crate; a panic
      resets the decoder.
  - **Pipeline:**
    - `AtmosPlayback` reads the TrueHD track through its own `MatroskaDemuxer` on a decode thread.
    - Media Foundation gets a video-only `MediaStreamSource`, opened with
      `MatroskaMediaSource.Open(…, externalAudioTrack)`.
    - Decoded audio goes into `AtmosStream`, a 4 s ring buffer anchored to the first packet's media
      time.
    - `SpatialAtmosOutput` sends static bed channels to static objects and everything else to
      dynamic `ISpatialAudioObject`s. Dolby Atmos for Headphones endpoints typically offer 128.
  - **Sync:** audio follows the video clock. The UI samples `PlaybackSession.Position` every 50 ms
    and the output extrapolates between samples. Drift is absorbed by resampling up to ±0.3%;
    anything over 40 ms is a hard jump. Output latency is estimated at 40 ms, and `[` / `]` shift
    lip-sync by 10 ms.
  - **Starts and seeks:** audio is audible within about 1 ms of the first video frame. TrueHD can
    only start decoding at a major sync (≤ about 107 ms apart), and positions arrive every 32 ms.
    - `AtmosPlayback` has its own `MatroskaDemuxer` with a 64 KB buffer, so skipped video blocks
      don't refill a large buffer.
    - On `Seeked(actualStart)` it decodes from the cue at least 250 ms earlier (pre-roll).
    - `MatroskaMediaSource.StartGate` defers Media Foundation's `Starting` request until audio has
      been decoded 200 ms past the start point (timeout 1.5 s).
    - The audio clock runs Held → Armed (on `StartReleased`) → Free. In Armed, the next "Playing"
      anchors it at the actual start; if none arrives, it falls back after 300 ms.
  - **Track choice:** with TrueHD objects available, a remux's TrueHD (lossless, with objects) beats
    a DD+ Atmos compatibility track for English.
- **Atmos lab** (`--atmos-lab x.atmos --bed p2.caf`): plays a truehdd DAMF decode and its 7.1
  downmix sample-locked in one spatial stream, for instant A/B. `T` runs a position test (noise
  circling, then rising overhead).
- **Atmos probe** (`MediaFoundation/AtmosProbe.cs`) drives Windows' DD+ decoder in
  `MFAudioFormat_Float_SpatialObjects` mode with real frames:
  - A JOC track keeps object mode and fails with `E_NOINTERFACE`, because only the audio renderer
    can allocate an `IMFSpatialAudioSample`.
  - Plain DD+ raises a format change and withdraws object output.
  - The MP4 `dec3` JOC flag is unreliable; trust the decoder.
- COM in the probe is called through raw vtable slots. The Windows SDK headers are the source of
  truth for GUIDs and vtable order; look them up rather than recalling them. They're in
  `C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um`, especially `mfapi.h` and
  `mfspatialaudio.h`.

## Repo layout

```
spike/EngineSpike/     WinUI 3 test harness: both engines, A/B, probes
  Mpv/                 libmpv P/Invoke and player wrapper
  MediaFoundation/     MF diagnostics and the Atmos probe
  Matroska/            Marquee's MKV demuxer and MediaStreamSource
  Interop/             Win32 child window that hosts mpv; Store codec runtime preload
  SpatialAudio/        Windows Spatial Sound output, TrueHD Atmos playback, Atmos lab
  samples.txt          your test files, label|path (gitignored; copy samples.example.txt)
native/truehd-ffi/     Rust C ABI over the truehd crate (TrueHD Atmos)
native/dovi-ffi/       Rust C ABI over the dolby_vision crate (DV RPU conversion)
tools/                 fetch-libmpv.ps1, fetch-ffmpeg.ps1, build-native.ps1
lib/                   libmpv-2.dll, ffmpeg/, truehd/, dovi/ (gitignored, fetched or built)
mise.toml              Rust toolchain pin
```

## Build, run, test

```powershell
mise install                      # Rust 1.99.1, pinned in mise.toml
./tools/fetch-libmpv.ps1
./tools/fetch-ffmpeg.ps1
./tools/build-native.ps1          # cargo build → lib/truehd/marquee_truehd.dll, lib/dovi/marquee_dovi.dll
dotnet build spike/EngineSpike -p:Platform=x64
spike/EngineSpike/bin/x64/Debug/net9.0-windows10.0.26100.0/win-x64/EngineSpike.exe
```

- CLI: `--engine mpv|mf`, `--start SECONDS`, `--mute`, `--fullscreen`, `--exit-after N` (also adds
  a 3-second position heartbeat), `--probe`, `--native-mkv`, `--no-dv`, `--audio-track N`
  (0-based index in Media Foundation's track list), `--ffmpeg-dts`, `--truehd-atmos`, then the
  path. Two other modes:
  - `--extract-track N --start S --seconds D --out f.thd <mkv>` writes raw packets, for example a
    TrueHD clip for truehdd.
  - `--atmos-lab x.atmos --bed p2.caf` opens the Atmos lab.
  - `--dv-info out.txt <mkv>` writes the Dolby Vision configuration and first RPU (e.g. P7 FEL vs
    MEL) and exits.
- With `--mute`, TrueHD Atmos output is silent but still decodes and syncs, and the heartbeat logs
  sync error, rate, resyncs and underruns.
- To see which codecs are really in use, list the running process's loaded modules
  (`(Get-Process -Id <pid>).Modules`): `DTSXDecoder.dll`, `DolbyVisionPlugin.dll`,
  `avcodec-63.dll`.
- Keys: F2 swaps engine at the same position and audio language, F3 picks the next audio track,
  F11/Esc toggle full screen.
- Logs: `%LOCALAPPDATA%\Marquee\EngineSpike\spike.log` (everything shown in the status pane) and
  `mpv.log`. For unattended runs use `--mute --exit-after 12` and read the logs.
- Only a human can verify Dolby Vision mode on the display (its own menu or indicator) and audible
  Atmos heights. Ask; don't assume.
- A running spike window locks the exe; close it before rebuilding.

## Environment

Machine-specific details live in `CLAUDE.local.md`, which is gitignored: hardware, network paths,
the media server's address, audio endpoint names. What the code assumes about any setup:

- **Display:** Windows 11 x64 with an HDR, Dolby Vision-capable display. Dolby Vision can work over
  DisplayPort, not only HDMI, and some monitors need it enabled in their own menu. Windows may
  under-report a panel's peak luminance; mpv's `target-peak` corrects that.
- **Spatial audio:** a Windows spatial format (e.g. Dolby Atmos for Headphones via Dolby Access)
  active on the output endpoint. Headset endpoints are often stereo, with the spatial renderer
  synthesizing 7.1 and objects, and WASAPI exclusive mode bypasses it.
- **Audio endpoints:** their GUIDs change when Windows re-enumerates a device, so pin them by name.
- **Media:** UNC paths work everywhere. Mapped drives can hang everything when the SMB redirector
  stalls.
- Debugging: WinDbg's `cdb` is under `C:\Program Files\WindowsApps\Microsoft.WinDbg_*\amd64`.
  Attach non-invasively with `cdb -pv -p <pid> -y C:\nosymbols -c "~*kc 18; qd"`. Downloading from
  the symbol server takes minutes and keeps the target suspended. `dumpbin` ships with VS 2022
  Community.

## Gotchas learned the hard way

- An unreferenced `DispatcherQueueTimer` gets garbage-collected and silently stops firing. Keep
  timers in fields.
- `MediaPlaybackItem(source, startTime)` trims the item: positions become relative and nothing
  before the start is reachable. Seek after `MediaOpened` instead.
- `AudioEncodingProperties.IsSpatial` is false even for Atmos tracks, so it's useless as a check.
- Matroska blocks can lace several audio frames; some DTS-HD MA tracks have 8 per block. Windows' decoders
  accept the frames concatenated, but FFmpeg's DTS decoder decodes only the first frame of a
  buffer. Feed laced frames one at a time (`MatroskaPacket.Frames()`), or playback races ahead.
- A TrueHD block is a single 40-sample access unit. Batch the decoded PCM into ~40 ms samples
  instead of handing Media Foundation over a thousand samples a second.
- After a seek, `MediaPlaybackSession` misleads an external audio clock in two ways:
  - It fires "Playing" in response to `Play()` while a deferred `MediaStreamSource.Starting` is
    still holding the start.
  - Once frames flow, it reports the *requested* position (e.g. 10:00.000) for a while, although
    it presents from the keyframe passed to `SetActualStartPosition` (e.g. 9:54.719).
  
  Anchor to the actual start after the start is released, and ignore polled positions for about a
  second.
- libmpv's defaults differ from mpv.exe: no config, OSC or input bindings unless they're set before
  `mpv_initialize`.

## Conventions

- Write comments only where the logic isn't obvious.
- Never add AI attribution to commits, PRs or anything else: no Co-Authored-By trailers and no
  "generated with" footers.
- Commit only when asked. The repo has no remote yet.
- Licensing: shinchiro's libmpv/FFmpeg builds are GPL. That's fine while the project is personal;
  a closed-source release would need LGPL builds. The separate FFmpeg used for audio decoding is
  already an LGPL build. The `truehd` crate is Apache-2.0, but it is a reverse-engineered
  implementation of Dolby's format, so distributing it would be a legal question, not a license
  one.
