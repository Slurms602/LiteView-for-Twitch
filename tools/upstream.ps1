# Windows wrapper: runs tools/upstream.sh with the bash that comes with Git for Windows.
#   powershell -ExecutionPolicy Bypass -File tools\upstream.ps1 status
$ErrorActionPreference = 'Stop'
$gitCore = (& git --exec-path).Trim()                       # ...\Git\mingw64\libexec\git-core
$bash = Join-Path (Split-Path (Split-Path (Split-Path $gitCore))) 'bin\bash.exe'
if (-not (Test-Path $bash)) { throw "Git for Windows bash not found (looked for $bash)" }
& $bash (Join-Path $PSScriptRoot 'upstream.sh') @args
exit $LASTEXITCODE
