# Removes the per-user install made by tools/install.ps1 (copied next to the app, and run by
# Settings > Apps > Uninstall). The library and settings in %LOCALAPPDATA%\Marquee are kept.
$ErrorActionPreference = 'Stop'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Marquee'

Get-Process Marquee -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dest\*" } | ForEach-Object {
    $_ | Stop-Process -Force
    $_ | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'Marquee.lnk') -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Marquee' -Recurse -ErrorAction SilentlyContinue
Set-Location ([IO.Path]::GetTempPath())
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
