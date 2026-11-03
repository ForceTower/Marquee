param(
    [string]$Branch = '9.0'
)

# Shared LGPL FFmpeg libraries for the Media Foundation engine: audio and bitmap subtitle decoding
# (TrueHD, DTS, PGS), MPEG-TS demuxing and MP4 subtitle reading. The branch must match the
# FFmpeg.AutoGen package version in Marquee.Playback.csproj.
$ErrorActionPreference = 'Stop'
$lib = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib\ffmpeg'
$asset = "ffmpeg-n$Branch-latest-win64-lgpl-shared-$Branch.zip"
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$asset"

$work = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    $archive = Join-Path $work $asset
    Write-Host "Downloading $asset"
    Invoke-WebRequest $url -OutFile $archive -UseBasicParsing
    Expand-Archive $archive -DestinationPath $work

    $bin = Get-ChildItem $work -Recurse -Directory -Filter bin | Select-Object -First 1
    if (Test-Path $lib) { Remove-Item $lib -Recurse -Force }
    New-Item -ItemType Directory -Force $lib | Out-Null
    Get-ChildItem $bin.FullName -Filter *.dll |
        Where-Object { $_.Name -match '^(avcodec|avformat|avutil|swresample)-\d+\.dll$' } |
        Copy-Item -Destination $lib
    Set-Content (Join-Path $lib 'ffmpeg.version') $asset
    Get-ChildItem $lib | ForEach-Object { Write-Host "  $($_.Name)" }
}
finally {
    Remove-Item $work -Recurse -Force
}
