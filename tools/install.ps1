# Builds a Release of Marquee, signs it and installs it for the current user: the app in
# %LOCALAPPDATA%\Programs\Marquee, a Start menu shortcut and an entry in Settings > Apps that
# uninstalls it. No administrator rights needed. Your library and settings live separately in
# %LOCALAPPDATA%\Marquee and are untouched by installing, updating or uninstalling.
#
# Signs Marquee's own binaries with the certificate from tools/new-signing-cert.ps1 when there is one
# (third-party libmpv/FFmpeg and Microsoft's runtime are left as they are). Run again to update.
param(
    [string]$Subject = 'CN=Marquee Local Signing',
    [string]$TimestampServer = 'http://timestamp.digicert.com',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts\publish'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Marquee'

if (-not $NoBuild) {
    Write-Host 'Publishing Release (self-contained, ReadyToRun)...'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    # The .NET SDK is pinned in mise.toml, so publish from the repository through mise.
    Push-Location $root
    try {
        mise exec -- dotnet publish "$root\src\Marquee\Marquee.csproj" -c Release -p:Platform=x64 -r win-x64 --self-contained `
            -p:PublishReadyToRun=true -o $publish -v quiet -nologo
        if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
    }
    finally {
        Pop-Location
    }
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $signtool) { throw 'signtool.exe not found; install the Windows SDK.' }
    $files = 'Marquee.exe', 'Marquee.dll', 'Marquee.Playback.dll', 'marquee_truehd.dll', 'marquee_dovi.dll' |
        ForEach-Object { Join-Path $publish $_ } | Where-Object { Test-Path $_ }
    Write-Host "Signing $($files.Count) files with $($cert.Subject) ($($cert.Thumbprint))..."
    & $signtool.FullName sign /q /fd SHA256 /sha1 $cert.Thumbprint /tr $TimestampServer /td SHA256 /d Marquee @files
    if ($LASTEXITCODE) { throw "signtool failed ($LASTEXITCODE)" }
} else {
    Write-Warning "No '$Subject' certificate; installing unsigned. tools/new-signing-cert.ps1 creates one."
}

$running = Get-Process Marquee -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" }
if ($running) {
    Write-Host 'Closing the installed Marquee...'
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Write-Host "Installing to $dest..."
robocopy $publish $dest /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $dest -Force

$exe = Join-Path $dest 'Marquee.exe'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Marquee.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exe
$link.WorkingDirectory = $dest
$link.Description = 'Marquee media player'
$link.Save()

$version = ((Get-Item $exe).VersionInfo.ProductVersion -split '\+')[0]
$sizeKb = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Marquee'
New-Item $key -Force | Out-Null
$values = @{
    DisplayName     = 'Marquee'
    DisplayVersion  = $version
    Publisher       = 'Marquee'
    DisplayIcon     = $exe
    InstallLocation = $dest
    InstallDate     = (Get-Date -Format 'yyyyMMdd')
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
}
foreach ($name in $values.Keys) { Set-ItemProperty $key -Name $name -Value $values[$name] }
foreach ($name in 'NoModify', 'NoRepair') { Set-ItemProperty $key -Name $name -Value 1 -Type DWord }
Set-ItemProperty $key -Name EstimatedSize -Value $sizeKb -Type DWord

Write-Host "Installed Marquee $version ($([math]::Round($sizeKb / 1KB)) MB). It's in the Start menu."
exit 0
