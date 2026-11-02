param(
    [string]$Arch = 'x86_64-v3',
    [string]$Tag
)

$ErrorActionPreference = 'Stop'
$repo = 'shinchiro/mpv-winbuild-cmake'
$lib = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib'

$releaseUrl = if ($Tag) { "https://api.github.com/repos/$repo/releases/tags/$Tag" } else { "https://api.github.com/repos/$repo/releases/latest" }
$release = Invoke-RestMethod $releaseUrl
$pattern = '^mpv-dev-' + [regex]::Escape($Arch) + '-\d{8}-git-[0-9a-f]+\.7z$'
$asset = $release.assets | Where-Object { $_.name -match $pattern } | Select-Object -First 1
if (-not $asset) { throw "No mpv-dev asset for $Arch in release $($release.tag_name)" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("libmpv-" + [guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    $archive = Join-Path $work $asset.name
    Write-Host "Downloading $($asset.name)"
    Invoke-WebRequest $asset.browser_download_url -OutFile $archive -UseBasicParsing
    tar -xf $archive -C $work
    if ($LASTEXITCODE -ne 0) { throw "tar failed to extract $archive" }

    New-Item -ItemType Directory -Force $lib | Out-Null
    Copy-Item (Join-Path $work 'libmpv-2.dll') $lib -Force
    Set-Content (Join-Path $lib 'libmpv.version') "$($release.tag_name) $($asset.name)"
    Write-Host "libmpv-2.dll -> $lib ($($release.tag_name))"
}
finally {
    Remove-Item $work -Recurse -Force
}
