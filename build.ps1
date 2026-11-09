# Builds LiteView for Twitch from source:
#   1. downloads the pinned third-party programs and checks them (SHA-256 / Microsoft signature)
#   2. compiles LiteView.exe and GameFirst.exe with the C# compiler that ships with Windows
#   3. assembles dist\LiteView-for-Twitch-<version>\ and the release zip
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 1.0.0]
param([string]$Version = '1.0.0')
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$cache = Join-Path $root 'build\cache'
$name  = "LiteView-for-Twitch-$Version"
$out   = Join-Path $root "dist\$name"
New-Item -ItemType Directory -Force $cache | Out-Null
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, "$out\twitch\streamlink", "$out\twitch\mpv" | Out-Null

$deps = @(
    @{ file = 'streamlink.zip'; sha = '3b1f1470d4b135080a8107ca2917379ca3ff5acadbcdc215568b79eea1450497'
       url  = 'https://github.com/streamlink/windows-builds/releases/download/8.6.3-1/streamlink-8.6.3-1-py315-x86_64.zip' },
    @{ file = 'mpv.zip'; sha = '8dd69cbab079086765fa5d873c09513b2834b85d146c1301718a757e8c0595ef'
       url  = 'https://github.com/mpv-player/mpv/releases/download/v0.42.0/mpv-v0.42.0-x86_64-pc-windows-msvc.zip' },
    @{ file = 'webview2.zip'; sha = $null   # checked below: every DLL must carry a valid Microsoft signature
       url  = 'https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/1.0.4314.35' }
)
foreach ($d in $deps) {
    $f = Join-Path $cache $d.file
    if (-not (Test-Path $f)) {
        Write-Host "Downloading $($d.file) ..."
        curl.exe -L --fail -s -o $f $d.url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($d.url)" }
    }
    if ($d.sha -and (Get-FileHash $f -Algorithm SHA256).Hash.ToLower() -ne $d.sha) { Remove-Item $f; throw "Checksum mismatch: $($d.file)" }
}

# WebView2 SDK (Microsoft): the three redistributable files
$wv = Join-Path $cache 'webview2'
if (-not (Test-Path $wv)) { Expand-Archive (Join-Path $cache 'webview2.zip') $wv }
$wvFiles = "$wv\lib\net462\Microsoft.Web.WebView2.Core.dll", "$wv\lib\net462\Microsoft.Web.WebView2.WinForms.dll", "$wv\runtimes\win-x64\native\WebView2Loader.dll"
foreach ($x in $wvFiles) {
    $sig = Get-AuthenticodeSignature $x
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') { throw "Not signed by Microsoft: $x" }
}
Copy-Item $wvFiles $out

# Compile (C# 5 compiler from the .NET Framework, present on every Windows 10/11 PC)
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$common = '/nologo', '/codepage:65001', '/target:winexe', '/optimize+', '/platform:x64', '/nowarn:649'
& $csc @common /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
    "/r:$out\Microsoft.Web.WebView2.Core.dll" "/r:$out\Microsoft.Web.WebView2.WinForms.dll" "/out:$out\LiteView.exe" "$root\src\liteview.cs" "$root\src\common.cs"
if ($LASTEXITCODE -ne 0) { throw 'LiteView.exe did not compile' }
& $csc @common "/out:$out\GameFirst.exe" "$root\src\guard.cs" "$root\src\common.cs"
if ($LASTEXITCODE -ne 0) { throw 'GameFirst.exe did not compile' }

# Programs and settings
tar.exe -xf (Join-Path $cache 'streamlink.zip') -C "$out\twitch\streamlink"
tar.exe -xf (Join-Path $cache 'mpv.zip') -C "$out\twitch\mpv"
Copy-Item "$root\config\mpv" "$out\twitch\mpv\portable_config" -Recurse
Set-Content "$out\twitch\mpv\portable_config\screen.conf" '' -Encoding ascii
Copy-Item "$root\config\gamefirst.ini", "$root\README.md", "$root\LICENSE", "$root\THIRD_PARTY_NOTICES.md" $out

# Zip
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
tar.exe -a -c -f $zip -C (Join-Path $root 'dist') $name
Write-Host ("Built {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
