# Builds LiteView-Enhanced-Setup-<version>.exe (installer/LiteView.iss) from the folder build.ps1 made:
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.1.0 -NoZip
#   powershell -ExecutionPolicy Bypass -File tools/make-installer.ps1 -Version 0.1.0
# Inno Setup is downloaded once (pinned version and SHA-256, the immutable GitHub release of its author) and installed
# for this build only into build\inno (no administrator rights, nothing added to the PC's Start menu).
param([string]$Version = '0.1.0', [string]$Source = '', [string]$OutDir = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
if (-not $Source) { $Source = Join-Path $root "dist\LiteView-Enhanced-$Version" }
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }
$Source = (Resolve-Path $Source).Path
if (-not (Test-Path (Join-Path $Source 'LiteView.exe'))) { throw "No LiteView.exe in $Source - run build.ps1 -Version $Version -NoZip first" }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$cache = Join-Path $root 'build\cache'; $inno = Join-Path $root 'build\inno'
New-Item -ItemType Directory -Force $cache | Out-Null
$setup = Join-Path $cache 'innosetup-6.7.4.exe'
$sha = 'a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c'
$url = 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe'
if (-not (Test-Path $setup)) {
    Write-Host 'Downloading Inno Setup 6.7.4 ...'
    curl.exe -L --fail -s --proto '=https' --tlsv1.2 -o "$setup.part" $url
    if ($LASTEXITCODE -ne 0) { Remove-Item "$setup.part" -ErrorAction SilentlyContinue; throw "Download failed: $url" }
    Move-Item "$setup.part" $setup
}
if ((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower() -ne $sha) { Remove-Item $setup; throw 'Inno Setup checksum mismatch (file deleted, run again)' }
$iscc = Join-Path $inno 'ISCC.exe'
if (-not (Test-Path $iscc)) {
    Write-Host 'Unpacking Inno Setup for this build ...'
    $p = Start-Process -FilePath $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', '/NOICONS', "/DIR=`"$inno`"" -Wait -PassThru
    if ($p.ExitCode -ne 0 -or -not (Test-Path $iscc)) { throw "Inno Setup could not be installed into $inno (exit $($p.ExitCode))" }
}
& $iscc "/DAppVersion=$Version" "/DSourceDir=$Source" "/O$OutDir" (Join-Path $root 'installer\LiteView.iss')
if ($LASTEXITCODE -ne 0) { throw 'The installer did not build' }
$exe = Join-Path $OutDir "LiteView-Enhanced-Setup-$Version.exe"
$h = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
Set-Content "$exe.sha256" "$h  LiteView-Enhanced-Setup-$Version.exe" -Encoding ascii
Write-Host ("Built {0} ({1:N0} MB)`nSHA-256 {2}" -f $exe, ((Get-Item $exe).Length / 1MB), $h)
