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

## Status (2026-11-03)

The real app (`src/Marquee`) exists: Plex PIN sign-in and sync, local folders with TMDB matching, a
SQLite library, the Home / Movies / TV / detail / Sources / Settings screens, and the player on both
engines. `spike/EngineSpike` remains the engine test harness. Engine work proven in the spike
(details and tables in README.md):

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
- **Subtitles in the Media Foundation engine** (2026-11-03), so subtitles no longer force mpv and
  HDR10:
  - SRT, WebVTT, ASS/SSA, PGS, VobSub and DVB, embedded or as files.
  - Rendered by libass and FFmpeg's bitmap decoders, as in mpv, into a XAML layer over the video.
  - Checked by rendering frames from real WEB-DLs, UHD remuxes and anime ASS tracks, and in the app
    over real Dolby Vision. Not yet checked by a human for timing.

Next:

1. Matroska chapters in the MKV reader (chapters currently come from Plex or mpv).
2. Display handling: HDR on/off and refresh-rate matching.
3. .NET 10 LTS (only the .NET 9 SDK is installed; .NET 9 support ends November 2026).

## The app (`src/Marquee`)

- **Shell:** `Shell/MainWindow` is Mica + the WinUI `TitleBar` control (search box in it) +
  `NavigationView`. Pages live in a `Frame`; Home and Library cache themselves and restore scroll.
  The player is a full-window layer over the shell (`ShowPlayerLayer`), not a page.
- **Look:** the design is a dark graphite / amber canvas ("Nightfall" was the designer's placeholder
  name). `Themes/Palette.xaml` overrides WinUI's own resources (system accent colors, button,
  combo box, nav and expander brushes) so native controls take the palette; `Themes/Styles.xaml`
  holds text and button styles; `Themes/Generic.xaml` the `SettingsCard` template. Icons are Segoe
  Fluent Icons glyphs; the app icon is rendered by `tools/make-icon.ps1`.
- **Data:** `Data/LibraryDatabase.cs`, SQLite via Microsoft.Data.Sqlite at
  `%LOCALAPPDATA%\Marquee\library.db`. One `items` table for movies/shows/seasons/episodes (ids
  `p:{server}:{ratingKey}` for Plex, `f:{hash}` / `fs:{hash}` for files), file versions and cached
  details (cast, streams, chapters, markers) as JSON columns, `matches` for TMDB, `meta` for
  settings and secrets. Plex and TMDB tokens are DPAPI-encrypted (`Data/Secrets.cs`). Schema is
  versioned with `PRAGMA user_version`; migrations are idempotent `CREATE ... IF NOT EXISTS`.
- **Plex:** `Plex/PlexService` (PIN sign-in, `plex.tv/api/v2/resources`, a connection chosen by
  probing local → remote → relay), `PlexServerApi` (paged listings, metadata, timeline, scrobble),
  `PlexMapper` (JSON → rows). Image references are stored as `plex:{server}:{path}` and resolved
  to the server's photo transcoder at display time, so a server changing address breaks nothing.
  Direct play is the part URL plus the token; the MKV reader reads it through `HttpRangeStream`.
- **Sync:** `Library/LibrarySync` runs a full Plex pass (types 1–4) and On Deck at startup and
  every 30 min, rescans watched folders on change (debounced), then `TmdbMatcher` matches local
  items. A rescan re-applies stored matches without calling TMDB; manual matches are locked.
  Local artwork next to the files beats TMDB's.
- **Player:** `Player/PlaybackPlanner` picks the version, audio (preferred language → Atmos →
  lossless → channels), subtitles (off / forced-only / always), resume point and engine.
  Automatic routing uses Media Foundation only when it adds something (real DV, Atmos objects,
  DTS:X) and can show the chosen subtitles (embedded ones need Marquee's MKV reader, so MP4 tracks
  still go to mpv). `MpvEngine` and `MediaFoundationEngine` implement
  `IPlayerEngine`; `PlayerView` runs a 50 ms tick, reports Plex timeline/progress every 10 s,
  scrobbles at 90 % or the final credits, shows skip-intro/credits from markers and counts down
  to the next episode. The user's star rating (`RatingControl`, Plex's 0–10 `userRating`) syncs
  through `/:/rate`; local items keep theirs across rescans.
- **Controls over mpv:** XAML can't draw over mpv's child HWND, so the controls live in
  `Player/OverlayWindow`: a borderless, owned, non-activating top-level window kept over the video,
  hosting a second `PlayerControls` bound to the same view model. Over Media Foundation the
  controls are inline. While the controls are auto-hidden, `Player/CursorBlocker` (an invisible
  Win32 window over the stage) hides the mouse cursor.
- **Logs:** `%LOCALAPPDATA%\Marquee\marquee.log` (startup, playback decisions, MKV reader,
  errors) and `mpv.log` next to it.

## Tech stack

- C# on .NET 9 (the only SDK installed; it goes out of support in November 2026, so the real app
  should target .NET 10 LTS). WinUI 3 via Windows App SDK 2.6.4, unpackaged
  (`WindowsPackageType=None`), self-contained, x64 only.
- App packages: Microsoft.Data.Sqlite and System.Security.Cryptography.ProtectedData 9.0.22,
  CommunityToolkit.Mvvm 8.4.2.
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
- **libass** 0.17.5 renders ASS/SSA subtitles (and SRT converted to ASS) for the Media Foundation
  engine. It's separate from the libass inside libmpv.
  - `tools/build-libass.ps1` builds one `lib/libass/libass.dll` with meson + MSVC. FreeType,
    FriBidi, HarfBuzz and libunibreak are pinned meson wraps, linked in statically.
  - Fonts come from the DirectWrite provider (no fontconfig); the assembly is on (nasm).
  - libunibreak provides `ASS_FEATURE_WRAP_UNICODE` (Unicode line breaking). It's on for converted
    text, so Japanese and Chinese lines without spaces wrap. ASS scripts keep VSFilter's breaking
    at spaces, which they're authored for.
  - It's built without iconv, so input must be UTF-8.
  - P/Invoke lives in `Subtitles/LibAss.cs`.
- **Subtitles for Media Foundation** (`Marquee.Playback/Subtitles/`, `Player/SubtitleOverlay`).
  This is mpv's approach without mpv's code:
  - **Text:** SRT and WebVTT become ASS events under a 1080p Segoe UI style (`TextSubtitles`).
    - Markup converts: i/b/u/s, font colour and face.
    - `{\an8}` and other override blocks pass through; literal braces are escaped as `\{`.
    - libass renders them, and ASS/SSA, at the overlay's own resolution (`AssRenderer`).
    - As with mpv's defaults, converted text may sit in the black bars and scales with the window,
      not the picture.
  - **Bitmaps:** PGS, VobSub and DVB go through FFmpeg's decoders (`BitmapSubtitleRenderer`).
    - A PGS set can reuse objects from earlier sets in its epoch, so after a jump decoding
      restarts at the last epoch start or acquisition point.
    - A canvas the shape of the video maps onto it. A 1080p canvas over a cropped encode is scaled
      to the video's width and centred, as if the bars were still there.
  - **Packets come through the MKV's index.** mkvmerge writes a cue entry for every subtitle block,
    with `CueRelativePosition` (the block's offset in its cluster) and `CueDuration`.
    - `MatroskaSubtitleSource` reads exactly the blocks from 10 s before to 60 s after the playing
      time, through its own 16 KB-buffered demuxer.
    - So seeks and track switches show lines that are already on screen.
    - Without those entries it taps the playback demuxer (`MatroskaDemuxer.Tap`) and misses lines
      that began before a seek's keyframe.
  - **Fonts and encodings:** fonts attached to the MKV go to libass. File encodings are detected
    (BOM, strict UTF-8, else Windows-1252), because libass only takes UTF-8.
  - **Timing:** `SubtitlePlayback` renders on its own thread, once per video frame, against the
    `PresentationClock` (see the TrueHD Atmos sync notes).
  - **Drawing:** `SubtitleOverlay` draws the newest frame as `WriteableBitmap`s on a Canvas over
    `MediaPlayerElement`. That's XAML content, so in HDR it shows at Windows' SDR brightness.
  - Embedded MP4 subtitle tracks still go to mpv.
  - `EngineSpike --sub-test <folder> [--sub-track N] [--start S] <mkv>` lists a file's subtitle
    tracks and their index coverage, then renders each around `--start` to PNGs plus a report.
    Given a .srt/.vtt/.ass/.ssa file instead, it renders that file's first cues from `--start`.
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
    - The video clock (`MediaFoundation/PresentationClock`, shared with subtitles) runs Held →
      Armed (on `StartReleased`) → Free. In Armed, the next "Playing" anchors it at the actual
      start; if none arrives, it falls back after 300 ms.
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
src/Marquee/              The app (WinUI 3)
  Shell/                  MainWindow, theme accent, formatting, log
  Pages/                  Home, Library, movie/show detail, Sources, Settings, match dialog
  Controls/               Poster/episode cards (hover lift + glow), episode row, SettingsCard
  Player/                 Planner, engines, PlayerView, PlayerControls, overlay window, cursor blocker,
                          subtitle overlay
  Data/                   SQLite library, models, settings, DPAPI secrets
  Plex/                   plex.tv + Plex Media Server API, JSON mapping
  Library/                Sync, folder scanner, file-name parser, TMDB, details, watch state
  Imaging/                Disk image cache, Img.Source attached property, average color
  Themes/                 Palette (overrides WinUI resources), styles, Generic.xaml
  Assets/                 App icon (generated by tools/make-icon.ps1)
src/Marquee.Playback/     Engine library shared by the app and the spike
  Mpv/                    libmpv P/Invoke and player wrapper
  MediaFoundation/        MF diagnostics, the Atmos probe, the presentation clock
  Matroska/               Marquee's MKV demuxer and MediaStreamSource
  IO/                     File/URL opening, HttpRangeStream for Plex direct play
  Interop/                Win32 child window that hosts mpv; Store codec runtime preload
  SpatialAudio/           Windows Spatial Sound output, TrueHD Atmos playback
  Subtitles/              libass and FFmpeg subtitle renderers, sources, the subtitle thread
spike/EngineSpike/        WinUI 3 test harness: both engines, A/B, probes, Atmos lab
  samples.txt             your test files, label|path (gitignored; copy samples.example.txt)
native/truehd-ffi/        Rust C ABI over the truehd crate (TrueHD Atmos)
native/dovi-ffi/          Rust C ABI over the dolby_vision crate (DV RPU conversion)
tools/                    fetch-libmpv.ps1, fetch-ffmpeg.ps1, build-native.ps1, build-libass.ps1,
                          make-icon.ps1
lib/                      libmpv-2.dll, ffmpeg/, truehd/, dovi/, libass/ (gitignored, fetched or built)
mise.toml                 Rust toolchain and libass build tool pins
```

The engine code is `internal`; `Marquee.Playback` grants `InternalsVisibleTo` to `Marquee` and
`EngineSpike`. Its csproj carries the native DLLs, which flow into both outputs.

## Build, run, test

```powershell
mise install                      # Rust, plus Python/uv/meson/ninja/nasm for libass (mise.toml)
./tools/fetch-libmpv.ps1
./tools/fetch-ffmpeg.ps1
./tools/build-native.ps1          # cargo build → lib/truehd/marquee_truehd.dll, lib/dovi/marquee_dovi.dll
./tools/build-libass.ps1          # meson + MSVC → lib/libass/libass.dll
dotnet build src/Marquee -p:Platform=x64
src/Marquee/bin/x64/Debug/net9.0-windows10.0.26100.0/win-x64/Marquee.exe
dotnet build spike/EngineSpike -p:Platform=x64
spike/EngineSpike/bin/x64/Debug/net9.0-windows10.0.26100.0/win-x64/EngineSpike.exe
```

- App test flags: `--page home|movies|shows|sources|settings`, `--open "title"` (detail page of
  the best search match), `--play "title"` (resumes it, or a show's next episode), `--mute`.
  Before `--play`: `--subtitle N` starts with that subtitle ordinal whatever the preferences say,
  and `--start SECONDS` starts there and stops progress reporting, so a test run doesn't move the
  resume point in the library or on Plex.
- Seeing the app without clicking: `PrintWindow(hwnd, …, PW_RENDERFULLCONTENT)` captures the main
  window from a **DPI-aware** process (a DPI-unaware one gets a virtualized, cropped rect). It
  doesn't include the mpv overlay window; capture the screen region for that.
- A running Marquee locks its exe; close it before rebuilding. `dotnet` prints in the Windows
  display language, so grep build logs for ` error ` rather than English text.

- CLI: `--engine mpv|mf`, `--start SECONDS`, `--mute`, `--fullscreen`, `--exit-after N` (also adds
  a 3-second position heartbeat), `--probe`, `--native-mkv`, `--no-dv`, `--audio-track N`
  (0-based index in Media Foundation's track list), `--ffmpeg-dts`, `--truehd-atmos`, then the
  path. Other modes:
  - `--extract-track N --start S --seconds D --out f.thd <mkv>` writes raw packets, for example a
    TrueHD clip for truehdd.
  - `--sub-test <folder> [--sub-track N] [--start S] <mkv or subtitle file>` renders subtitles to
    PNGs.
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
  second. `PresentationClock` does this for anything timed to Media Foundation's video (TrueHD
  Atmos, subtitles); `MatroskaMediaSource` raises `StartReleased` even when no start gate holds it.
- Many UHD Blu-ray PGS tracks are authored grey, not white, so they aren't glaring over HDR. mpv
  shows them grey too; that isn't a palette bug.
- A WEB-DL MKV usually carries a "Forced" text track with only a handful of events (signs, foreign
  dialogue), so a forced track that renders nothing at a given minute is normal.
- libmpv's defaults differ from mpv.exe: no config, OSC or input bindings unless they're set before
  `mpv_initialize`. mpv.conf loads *during* `mpv_initialize`, so the app sets its own settings as
  properties afterwards to win over it.
- A transparent WinUI window needs two things: a `SystemBackdrop` that sets a transparent
  `Windows.UI.Composition` color brush, **and** `DwmExtendFrameIntoClientArea(-1)`. The backdrop
  alone leaves the window opaque black; `WS_EX_LAYERED` doesn't help. Transparent areas then pass
  the mouse through, so `PlayerView` polls the cursor (`GetCursorPos` + `WindowFromPoint` →
  `GetAncestor(GA_ROOT)`) to show the controls; `WindowFromPoint` returns the XAML island's child
  window, not the top-level one.
- mpv's video window belongs to mpv's thread: `ShowCursor` from the UI thread doesn't hide the cursor
  over it; set mpv's `cursor-autohide` too.
- XAML can't hide the cursor under a still pointer. WinUI sets cursors from its own input thread
  (`Microsoft.InputStateManager`, with a `LiftedDMITCursorRecalculateClass` message window), so
  `ShowCursor`/`SetCursor` from the UI thread are overridden. A `ProtectedCursor` change is only
  honoured on real pointer movement, and every later recalculation (any layout change in the
  controls triggers one) puts the arrow back. That held for a blank `ProtectedCursor` on an overlay
  element, on the window root, and with a `SendInput` nudge. Only a plain Win32 window works:
  `CursorBlocker` lays an invisible (`WS_EX_NOREDIRECTIONBITMAP`, no class cursor) window over the
  stage while the controls are hidden, and the first movement, click or wheel turn removes it.
- NVIDIA's overlay (the NVIDIA app) injects `nvspcap64.dll` into every Direct3D process and takes a
  full-screen video swap chain for a game ("Press Alt+Z" banner). There's no API for it. The
  driver profile setting 0x809D5F60 ("NVIDIA App Overlay - Flags" in Profile Inspector) set to
  0x10000000 keeps the hook out of the executable; checked by the DLL no longer loading. Creating
  the profile and reading the setting work as a normal user, but `NvAPI_DRS_SetSetting` returns
  `NVAPI_INVALID_USER_PRIVILEGE` (-137) without admin. That's why `Player/NvidiaOverlay` runs
  `Marquee.exe --nvidia-overlay-off` elevated from the Settings card.
- Media Foundation's clock follows its audio renderer. An output that accepts samples but never
  plays them freezes the picture at 0:00 while the player still reports Playing, at idle CPU. One
  example is an S/PDIF port with nothing attached, which Windows makes the default when a headset
  switches off. `PlayerView.WatchAudio` spots a clock that's stuck for 3 s and reopens video-only
  (`PlaybackRequest.WithoutSound`). It retries sound when Windows' default output changes or a
  track is picked. mpv drops the audio track itself (`aid=no`) and keeps playing; Marquee only
  says so.
- x:Bind doesn't call a binding function whose argument is null; the target keeps its old value.
  `Visibility="{x:Bind Bind.VisText(ViewModel.Message)}"` left an empty message box on screen
  after the text cleared. Bind visibilities to bools (`HasMessage`, `HasSkip`, `HasThumb`).
- WinUI 3's XAML compiler doesn't support `x:ClassModifier`; XAML classes are public. A record used
  as `x:DataType` gets a generated parameterless activator, so `required` members break the build.
- CommunityToolkit.Mvvm 8.4's partial-property `[ObservableProperty]` needs a preview C# version on
  .NET 9; the view model uses the field form.
- `ItemsRepeater` children honour `Canvas.ZIndex`, which is how a hovered card lifts above its
  neighbours. A standard `Button` template clips and paints on hover; cards use `CardButtonStyle`.
- Image backdrops fading into the page need a few percent of fully opaque gradient at the edge:
  at 125 % scale the last pixel row otherwise peeks out while scrolling.
- KeyboardAccelerators on a root element show "Ctrl+F"-style tooltips everywhere; set
  `KeyboardAcceleratorPlacementMode="Hidden"`.
- PowerShell unrolls a `byte[]` returned from a function into the pipeline; return `, $bytes`.
- Plex: full metadata JSON has keys differing only in case (`guid` and `Guid`), which PowerShell's
  `ConvertFrom-Json` rejects without `-AsHashtable`. `audienceRating` is Rotten Tomatoes' audience
  score out of 10. Audio streams flag Atmos in `profile` ("dolby truehd + dolby atmos") and
  `displayTitle`; video streams carry `DOVIPresent`/`DOVIProfile` and `colorTrc`.
- TMDB has no keyless access. The user supplies a key in Settings → Library (an API key goes in
  `api_key=`, a read access token as a bearer token).

## Conventions

- Write comments only where the logic isn't obvious.
- Never add AI attribution to commits, PRs or anything else: no Co-Authored-By trailers and no
  "generated with" footers.
- Commit only when asked. The repo is public on GitHub (`origin`,
  `git@github.com:ForceTower/Marquee.git`), so commits carry no personal setup: no IPs, paths,
  hardware or endpoint names, real titles or release names. Those live in `CLAUDE.local.md`; describe
  test files by format ("DV P8 MKV WEB-DL", "UHD remux P7 FEL").
- Licensing: Marquee is GPL-3.0 (`LICENSE`), which suits the GPL libmpv build it bundles
  (shinchiro's). `THIRD-PARTY-NOTICES.md` lists the third-party work and its licenses. The separate
  FFmpeg used for audio decoding is an LGPL build. The `truehd` crate is Apache-2.0, but it is a
  reverse-engineered implementation of Dolby's format, so distributing it would be a legal
  question, not a license one.
