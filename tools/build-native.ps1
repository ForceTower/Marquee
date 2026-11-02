# Builds Marquee's native Rust libraries (toolchain pinned in mise.toml) and copies each DLL into
# lib/<name>, where EngineSpike.csproj picks them up:
#   native/truehd-ffi → lib/truehd/marquee_truehd.dll  (TrueHD Atmos decoding, truehd crate)
#   native/dovi-ffi   → lib/dovi/marquee_dovi.dll      (Dolby Vision RPU conversion, dolby_vision crate)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$crates = @(
    @{ Crate = 'truehd-ffi'; Dll = 'marquee_truehd.dll'; Lib = 'truehd' },
    @{ Crate = 'dovi-ffi'; Dll = 'marquee_dovi.dll'; Lib = 'dovi' }
)

foreach ($c in $crates) {
    $crate = Join-Path $root "native\$($c.Crate)"
    Push-Location $crate
    try {
        mise exec -- cargo build --release
        if ($LASTEXITCODE -ne 0) { throw "cargo build failed for $($c.Crate)" }
    }
    finally {
        Pop-Location
    }

    $lib = Join-Path $root "lib\$($c.Lib)"
    New-Item -ItemType Directory -Force $lib | Out-Null
    Copy-Item (Join-Path $crate "target\release\$($c.Dll)") $lib -Force
    Write-Host "$($c.Dll) -> $lib"
}
