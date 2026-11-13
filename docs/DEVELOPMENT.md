# Development

## Requirements

- **To build**: the .NET SDK 10 (`winget install Microsoft.DotNet.SDK.10`). The app still targets .NET Framework 4.8,
  which is part of Windows; the SDK only compiles it.
- **To package and run**: Windows 10/11 64-bit (`build.ps1` uses `curl.exe`, `tar.exe`, Authenticode checks).
- Git for Windows (also gives the bash used by `tools/upstream.sh`).

The pure-logic library and its tests also build and run on Linux and macOS (`dotnet test`), and the whole solution
compiles there against .NET Framework reference assemblies, which is how CI checks it.

## Commands

| What | Command |
|---|---|
| Build everything | `dotnet build LiteView.sln -c Release` |
| Unit tests | `dotnet test LiteView.sln -c Release` |
| Development run | `dotnet build src\app -c Debug`, then copy `twitch\` (Streamlink + mpv) from a release next to `src\app\bin\Debug\net48\LiteView.exe`, or run the packaged build |
| Release folder + zip | `powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.1.0` → `dist\` |
| Quick package without tests or zip | `build.ps1 -SkipTests -NoZip` |
| Upstream review | `tools/upstream.sh status` (see `docs/UPSTREAM.md`) |

`build.ps1` downloads Streamlink and mpv once into `build\cache` (SHA-256 checked), restores NuGet packages in
locked mode, runs the tests, compiles both programs, checks the WebView2 DLL signatures, refuses to package a
token/log/settings file, and writes `FILES.sha256` inside the folder and `<zip>.sha256` next to the zip.

## Layout and rules

See `docs/ARCHITECTURE.md` for the modules. Rules for changes:

1. **Keep upstream files at their paths** and keep edits to them small; put new code in `src/core` (logic, with
   tests) or `src/app` (UI). This keeps `tools/upstream.sh pick` working.
2. Logic that can be tested without Windows belongs in `src/core`, with tests in `tests/`.
3. Anything from outside (network, web pages, chat, files) is validated in `src/core` before use; links open only
   through `Launcher`.
4. Never log tokens or chat; use `Log`, which redacts.
5. Dependencies: pinned in `Directory.Packages.props`; after changing one, run `dotnet restore` (updates
   `packages.lock.json`) and commit both. Every new package needs a reason in the commit message and an entry in
   `THIRD_PARTY_NOTICES.md` if it ships.
6. `src/core` builds with warnings as errors and the recommended analyzers.

## Versions

`Directory.Build.props` holds the fork's version (`0.1.0`) and the upstream base (`1.2.0 (46e76df)`), both shown in
*Export diagnostics*.
