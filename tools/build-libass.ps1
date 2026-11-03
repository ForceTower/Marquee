# Builds libass (ASS/SSA subtitle rendering for the Media Foundation engine) as a single x64 DLL with
# meson + ninja + MSVC, and copies it to lib/libass/libass.dll. FreeType, FriBidi, HarfBuzz and
# libunibreak (Unicode line breaking, so text without spaces wraps) come from pinned meson wraps and
# are linked in statically; system fonts come through DirectWrite (no fontconfig). Python, uv, meson,
# ninja and nasm are pinned in mise.toml. Downloads, sources and the build tree live in
# native/libass/ (gitignored).
$ErrorActionPreference = 'Stop'
$version = '0.17.5'
$sha256 = 'caab4b993dd7be6187c55623b789ed75dddefea6e65938af134637c732fe094a'

$root = Split-Path $PSScriptRoot -Parent
$work = Join-Path $root 'native\libass'
$tarball = Join-Path $work "libass-$version.tar.gz"
$src = Join-Path $work "libass-$version"
$build = Join-Path $work 'build'
$lib = Join-Path $root 'lib\libass'

# WrapDB wraps, pinned. Each one carries its source tarball's SHA-256, which meson verifies.
$wraps = [ordered]@{
    freetype2 = @'
[wrap-file]
directory = freetype-2.15.0
source_url = https://download.savannah.gnu.org/releases/freetype/freetype-2.15.0.tar.xz
source_fallback_url = https://wrapdb.mesonbuild.com/v2/freetype2_2.15.0-1/get_source/freetype-2.15.0.tar.xz
source_filename = freetype-2.15.0.tar.xz
source_hash = 93cd47eb719c325107f2d3cf087d06299e944c3b48a40b55537302304dea1dd5
wrapdb_version = 2.15.0-1

[provide]
dependency_names = freetype2
'@
    fribidi = @'
[wrap-file]
directory = fribidi-1.0.18
source_url = https://github.com/fribidi/fribidi/releases/download/v1.0.18/fribidi-1.0.18.tar.xz
source_filename = fribidi-1.0.18.tar.xz
source_hash = 1de5e8073d035388b71d0b2d452b1da1f8caeea8a7702997b401fe1b6250727f
source_fallback_url = https://wrapdb.mesonbuild.com/v2/fribidi_1.0.18-1/get_source/fribidi-1.0.18.tar.xz
wrapdb_version = 1.0.18-1

[provide]
dependency_names = fribidi
'@
    harfbuzz = @'
[wrap-file]
directory = harfbuzz-13.0.1
source_url = https://github.com/harfbuzz/harfbuzz/releases/download/13.0.1/harfbuzz-13.0.1.tar.xz
source_filename = harfbuzz-13.0.1.tar.xz
source_hash = 3553d943401c34ab9b8c75f35cdb8452ca660233b0e9d4a22395ce5245484bd7
source_fallback_url = https://wrapdb.mesonbuild.com/v2/harfbuzz_13.0.1-1/get_source/harfbuzz-13.0.1.tar.xz
wrapdb_version = 13.0.1-1

[provide]
dependency_names = harfbuzz
'@
    libunibreak = @'
[wrap-file]
directory = libunibreak-6.1
source_url = https://github.com/adah1972/libunibreak/releases/download/libunibreak_6_1/libunibreak-6.1.tar.gz
source_filename = libunibreak-6.1.tar.gz
source_hash = cc4de0099cf7ff05005ceabff4afed4c582a736abc38033e70fdac86335ce93f
patch_filename = libunibreak_6.1-1_patch.zip
patch_url = https://wrapdb.mesonbuild.com/v2/libunibreak_6.1-1/get_patch
patch_hash = 830688953398bcb5ca8d88e619b0babbb31de95b31f47234adb0245bce95cf14
source_fallback_url = https://github.com/mesonbuild/wrapdb/releases/download/libunibreak_6.1-1/libunibreak-6.1.tar.gz
wrapdb_version = 6.1-1

[provide]
dependency_names = libunibreak
'@
}

$options = @(
    '--buildtype=release', '-Db_ndebug=if-release', '--default-library=shared',
    '--force-fallback-for=freetype2,fribidi,harfbuzz,libunibreak',
    '-Ddirectwrite=enabled', '-Dfontconfig=disabled', '-Dcoretext=disabled', '-Dlibunibreak=enabled',
    '-Dasm=enabled',
    '-Dtest=disabled', '-Dcompare=disabled', '-Dprofile=disabled', '-Dfuzz=disabled', '-Dcheckasm=disabled',
    '-Dfreetype2:default_library=static', '-Dfreetype2:zlib=internal', '-Dfreetype2:bzip2=disabled',
    '-Dfreetype2:png=disabled', '-Dfreetype2:brotli=disabled', '-Dfreetype2:harfbuzz=disabled',
    '-Dfreetype2:hvf=disabled',
    '-Dfribidi:default_library=static', '-Dfribidi:bin=false', '-Dfribidi:docs=false', '-Dfribidi:tests=false',
    '-Dharfbuzz:default_library=static', '-Dharfbuzz:glib=disabled', '-Dharfbuzz:gobject=disabled',
    '-Dharfbuzz:cairo=disabled', '-Dharfbuzz:chafa=disabled', '-Dharfbuzz:icu=disabled',
    '-Dharfbuzz:freetype=disabled', '-Dharfbuzz:raster=disabled', '-Dharfbuzz:vector=disabled',
    '-Dharfbuzz:subset=disabled', '-Dharfbuzz:tests=disabled', '-Dharfbuzz:introspection=disabled',
    '-Dharfbuzz:docs=disabled', '-Dharfbuzz:utilities=disabled',
    '-Dlibunibreak:default_library=static'
)

New-Item -ItemType Directory -Force $work | Out-Null
if (-not (Test-Path $tarball)) {
    Write-Host "Downloading libass $version"
    Invoke-WebRequest "https://github.com/libass/libass/releases/download/$version/libass-$version.tar.gz" -OutFile $tarball -UseBasicParsing
}
$actual = (Get-FileHash $tarball -Algorithm SHA256).Hash
if ($actual -ne $sha256) {
    Remove-Item $tarball
    throw "libass-$version.tar.gz has SHA-256 $actual, expected $sha256"
}

foreach ($dir in $src, $build) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
& "$env:SystemRoot\System32\tar.exe" -xzf $tarball -C $work
if ($LASTEXITCODE -ne 0) { throw "Extracting $tarball failed" }
$subprojects = Join-Path $src 'subprojects'
New-Item -ItemType Directory -Force $subprojects | Out-Null
foreach ($name in $wraps.Keys) {
    Set-Content (Join-Path $subprojects "$name.wrap") $wraps[$name] -Encoding ascii
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio with the x64 C++ tools was not found' }

# The developer shell changes this process's environment; restore it afterwards.
$savedEnv = @{}
Get-ChildItem env: | ForEach-Object { $savedEnv[$_.Name] = $_.Value }
Push-Location $root
try {
    $env:PATH = "$(Split-Path $vswhere);$env:PATH"
    & (Join-Path $vs 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation | Out-Null
    $env:CC = 'cl'
    $env:CXX = 'cl'
    $env:MESON_PACKAGE_CACHE_DIR = Join-Path $work 'packagecache'
    # libass stamps `git describe` into its version string; without a ceiling, git would describe
    # Marquee's repository instead.
    $env:GIT_CEILING_DIRECTORIES = $work

    mise exec -- meson setup $build $src @options
    if ($LASTEXITCODE -ne 0) { throw 'meson setup failed' }
    mise exec -- meson compile -C $build
    if ($LASTEXITCODE -ne 0) { throw 'meson compile failed' }
}
finally {
    Pop-Location
    Get-ChildItem env: | Where-Object { -not $savedEnv.ContainsKey($_.Name) } |
        ForEach-Object { Remove-Item "env:$($_.Name)" }
    foreach ($name in $savedEnv.Keys) { Set-Item "env:$name" $savedEnv[$name] }
}

$dll = Get-ChildItem (Join-Path $build 'libass') -Filter '*.dll' | Select-Object -First 1
if (-not $dll) { throw "No DLL was built in $build\libass" }
New-Item -ItemType Directory -Force $lib | Out-Null
Copy-Item $dll.FullName (Join-Path $lib 'libass.dll') -Force
Set-Content (Join-Path $lib 'libass.version') "libass $version (FreeType 2.15.0, FriBidi 1.0.18, HarfBuzz 13.0.1, libunibreak 6.1)"
Write-Host "$($dll.Name) -> $lib\libass.dll ($([math]::Round($dll.Length / 1MB, 2)) MB)"
