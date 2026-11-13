# Builds LiteView Enhanced from source:
#   1. downloads the pinned third-party programs and checks them (SHA-256 / Microsoft signature)
#   2. runs the unit tests, then compiles LiteView.exe and GameFirst.exe with the .NET SDK
#      (they still run on the .NET Framework 4.8 that is part of Windows 10/11: users install nothing)
#   3. assembles dist\LiteView-Enhanced-<version>\, the release zip and its SHA256SUMS.txt
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 0.1.0] [-SkipTests] [-NoZip]
# Needs:  the .NET SDK 10 (winget install Microsoft.DotNet.SDK.10), Windows 10/11 (curl.exe and tar.exe are built in).
param([string]$Version = '0.1.0', [switch]$SkipTests, [switch]$NoZip)
$ErrorActionPreference = 'Stop'
$root  = $PSScriptRoot
$cache = Join-Path $root 'build\cache'
$name  = "LiteView-Enhanced-$Version"
$out   = Join-Path $root "dist\$name"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is needed to build (not to run) LiteView: winget install Microsoft.DotNet.SDK.10"
}
New-Item -ItemType Directory -Force $cache | Out-Null
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out, "$out\twitch\streamlink", "$out\twitch\mpv" | Out-Null

# Pinned programs (same versions and hashes as upstream LiteView 1.2.0). Change a version = change its hash.
$deps = @(
    @{ file = 'streamlink.zip'; sha = '3b1f1470d4b135080a8107ca2917379ca3ff5acadbcdc215568b79eea1450497'
       url  = 'https://github.com/streamlink/windows-builds/releases/download/8.6.3-1/streamlink-8.6.3-1-py315-x86_64.zip' },
    @{ file = 'mpv.zip'; sha = '8dd69cbab079086765fa5d873c09513b2834b85d146c1301718a757e8c0595ef'
       url  = 'https://github.com/mpv-player/mpv/releases/download/v0.42.0/mpv-v0.42.0-x86_64-pc-windows-msvc.zip' }
)
foreach ($d in $deps) {
    $f = Join-Path $cache $d.file
    if (-not (Test-Path $f)) {
        Write-Host "Downloading $($d.file) ..."
        curl.exe -L --fail -s --proto '=https' --tlsv1.2 -o "$f.part" $d.url
        if ($LASTEXITCODE -ne 0) { Remove-Item "$f.part" -ErrorAction SilentlyContinue; throw "Download failed: $($d.url)" }
        Move-Item "$f.part" $f
    }
    if ((Get-FileHash $f -Algorithm SHA256).Hash.ToLower() -ne $d.sha) { Remove-Item $f; throw "Checksum mismatch (file deleted, run again): $($d.file)" }
}

# Tests, then the two programs. NuGet packages (WebView2 SDK) are pinned in Directory.Packages.props and
# checked against packages.lock.json (--locked-mode fails if anything differs).
Push-Location $root
try {
    dotnet restore LiteView.sln --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed (a package or its hash changed?)' }
    if (-not $SkipTests) {
        dotnet test LiteView.sln -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed: nothing was packaged' }
    }
    $bin = Join-Path $root 'build\bin'
    foreach ($p in 'src\app\LiteView.csproj', 'src\gamefirst\GameFirst.csproj') {
        dotnet build $p -c Release --no-restore "-p:Version=$Version" "-p:OutDir=$bin\"
        if ($LASTEXITCODE -ne 0) { throw "$p did not compile" }
    }
} finally { Pop-Location }

# WebView2 SDK files (Microsoft, redistributable): every one must carry a valid Microsoft signature
$wvFiles = "$bin\Microsoft.Web.WebView2.Core.dll", "$bin\Microsoft.Web.WebView2.WinForms.dll", "$bin\runtimes\win-x64\native\WebView2Loader.dll"
foreach ($x in $wvFiles) {
    $sig = Get-AuthenticodeSignature $x
    if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Microsoft') { throw "Not signed by Microsoft: $x" }
}
Copy-Item $wvFiles $out
Copy-Item "$bin\LiteView.exe", "$bin\LiteView.exe.config", "$bin\LiteView.Core.dll", "$bin\GameFirst.exe", "$bin\GameFirst.exe.config" $out

# Programs and settings
tar.exe -xf (Join-Path $cache 'streamlink.zip') -C "$out\twitch\streamlink"
if ($LASTEXITCODE -ne 0) { throw 'Unpacking Streamlink failed' }
tar.exe -xf (Join-Path $cache 'mpv.zip') -C "$out\twitch\mpv"
if ($LASTEXITCODE -ne 0) { throw 'Unpacking mpv failed' }
if (-not (Get-ChildItem "$out\twitch\streamlink" -Recurse -Filter streamlinkw.exe)) { throw 'streamlinkw.exe missing after unpacking' }
if (-not (Test-Path "$out\twitch\mpv\mpv.exe")) { throw 'mpv.exe missing after unpacking' }
Copy-Item "$root\config\mpv" "$out\twitch\mpv\portable_config" -Recurse
Set-Content "$out\twitch\mpv\portable_config\screen.conf" '' -Encoding ascii
Copy-Item "$root\config\gamefirst.ini", "$root\README.md", "$root\LICENSE", "$root\THIRD_PARTY_NOTICES.md", "$root\CHANGELOG.md" $out

# A token, settings or log must never end up in a release
$leak = Get-ChildItem $out -Recurse -File | Where-Object { $_.Name -match '\.(token|log)$|^liteview\.ini|^diagnostics-' }
if ($leak) { throw "Private files in the release folder: $($leak.FullName -join ', ')" }

# File list with hashes inside the folder, then the zip and its checksum next to it
$manifest = Get-ChildItem $out -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.FullName.Substring($out.Length + 1).Replace('\', '/')
}
Set-Content "$out\FILES.sha256" $manifest -Encoding ascii
if ($NoZip) { Write-Host "Built $out"; return }
$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
tar.exe -a -c -f $zip -C (Join-Path $root 'dist') $name
if ($LASTEXITCODE -ne 0) { Remove-Item $zip -ErrorAction SilentlyContinue; throw 'Creating the zip failed' }
$listed = (tar.exe -tf $zip | Measure-Object).Count
if ($LASTEXITCODE -ne 0 -or $listed -lt $manifest.Count) { throw "The zip looks incomplete ($listed entries for $($manifest.Count) files)" }
$h = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
Set-Content (Join-Path $root "dist\$name.zip.sha256") "$h  $name.zip" -Encoding ascii
Write-Host ("Built {0} ({1:N0} MB)`nSHA-256 {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $h)
