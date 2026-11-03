# Creates a self-signed code-signing certificate for local release builds and trusts it on this PC.
# The private key stays in your user certificate store and can't be exported. Only the public
# certificate goes into the machine's Trusted Root and Trusted Publishers stores, which is what makes
# the signatures valid here (Windows checks elevation prompts against the machine stores). That step
# needs administrator rights: one UAC prompt. Run once; tools/install.ps1 finds the certificate by
# its subject. -Replace makes a new one and drops the old ones from all three stores.
# To undo: remove it from those two stores (certlm.msc) and from certmgr.msc > Personal.
param(
    [string]$Subject = 'CN=Marquee Local Signing',
    [int]$Years = 25,
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'

if ($Replace) {
    Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Subject } | ForEach-Object {
        Write-Host "Removing certificate $($_.Thumbprint)"
        Remove-Item $_.PSPath -DeleteKey
    }
}

$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if ($cert) {
    Write-Host "Using existing certificate $($cert.Thumbprint), valid until $($cert.NotAfter.ToShortDateString())"
} else {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Subject -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears($Years)
    Write-Host "Created certificate $($cert.Thumbprint), valid until $($cert.NotAfter.ToShortDateString())"
}

$stores = 'Root', 'TrustedPublisher'
$missing = $stores | Where-Object { -not (Test-Path "Cert:\LocalMachine\$_\$($cert.Thumbprint)") }
$stale = $stores | ForEach-Object { Get-ChildItem "Cert:\LocalMachine\$_" } |
    Where-Object { $_.Subject -eq $Subject -and $_.Thumbprint -ne $cert.Thumbprint }
if (-not $missing -and -not $stale) {
    Write-Host 'Already trusted on this PC.'
    return
}

$cer = Join-Path ([IO.Path]::GetTempPath()) "marquee-signing-$($cert.Thumbprint).cer"
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
try {
    $commands = @($missing | ForEach-Object { "Import-Certificate -FilePath '$cer' -CertStoreLocation Cert:\LocalMachine\$_ | Out-Null" })
    $commands += $stale | ForEach-Object { "Remove-Item '$($_.PSPath)'" }
    Start-Process powershell.exe -Verb RunAs -Wait -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command', ($commands -join '; ')
} finally {
    Remove-Item $cer -ErrorAction SilentlyContinue
}

$missing = $stores | Where-Object { -not (Test-Path "Cert:\LocalMachine\$_\$($cert.Thumbprint)") }
if ($missing) {
    throw "The certificate isn't trusted yet (missing from LocalMachine\$($missing -join ', ')). Was the UAC prompt declined?"
}
foreach ($old in $stale) {
    if (Test-Path $old.PSPath) { Write-Warning "Couldn't remove old certificate $($old.Thumbprint) from the machine stores." }
    else { Write-Host "Removed old certificate $($old.Thumbprint) from the machine stores." }
}
Write-Host 'Trusted on this PC (LocalMachine\Root and LocalMachine\TrustedPublisher).'
