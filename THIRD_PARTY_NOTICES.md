# Third-party notices

LiteView for Twitch bundles the following programs and shaders. Each keeps its own licence; the
LiteView source code itself (src/, build.ps1, config/*.ini, config/mpv/*.conf) is MIT (see `LICENSE`).

| Component | Version | Licence | Source |
|---|---|---|---|
| Streamlink (Windows build, with embedded Python) | 8.6.3-1 | BSD-2-Clause (bundled packages: see their licence files inside `twitch/streamlink`) | https://github.com/streamlink/streamlink, https://github.com/streamlink/windows-builds/releases/tag/8.6.3-1 |
| mpv | 0.42.0 | GPL-2.0-or-later / LGPL-2.1-or-later | https://github.com/mpv-player/mpv/releases/tag/v0.42.0 |
| Microsoft WebView2 SDK (Core, WinForms, Loader DLLs) | 1.0.4314.35 | Microsoft WebView2 SDK licence (redistributable) | https://www.nuget.org/packages/Microsoft.Web.WebView2/1.0.4314.35 |
| AMD FidelityFX Super Resolution 1 (`FSR.glsl`), mpv port by agyild | 1.0.2 | MIT, (c) Advanced Micro Devices | https://gist.github.com/agyild/82219c545228d70c5604f865ce0b0ce5 |
| AMD FidelityFX CAS (`CAS.glsl`), mpv port by agyild | - | MIT, (c) Advanced Micro Devices | https://gist.github.com/agyild/bbb4e58298b2f86aa24da3032a0d2ee6 |
| FSRCNNX (`FSRCNNX_x2_8-0-4-1_any.glsl`, `FSRCNNX_x2_16-0-4-1_any.glsl`) by igv | 1.1 | GPL-3.0 | https://github.com/igv/FSRCNN-TensorFlow (the `_any` copies only change the minimum upscale factor from 1.3 to 1.0) |
| KrigBilateral (`KrigBilateral.glsl`) by igv | - | LGPL-3.0 | https://gist.github.com/igv/a015fc885d5c22e6891820ad89555637 |
| SSimDownscaler (`SSimDownscaler.glsl`) by igv | - | LGPL-3.0 | https://gist.github.com/igv/36508af3ffc84410fe39761d6969be10 |
| NLMeans (`nlmeans_sharpen_denoise.glsl`) by an3223 | - | LGPL-3.0 | https://github.com/AN3223/dotfiles/tree/master/.config/mpv/shaders |
| Adaptive Sharpen (`adaptive-sharpen.glsl`, `adaptive-sharpen_strong.glsl`) by bacondither, mpv port by igv | - | BSD-2-Clause | https://gist.github.com/igv/8a77e4eb8276753b54bb94c1c50c317e (`_strong` only raises `curve_height` from 1.0 to 1.5) |

Not bundled, loaded at run time from the user's own Chrome install or the Chrome Web Store:
**7TV** browser extension (https://7tv.app). (web chat only)

Not bundled, fetched at run time by the native chat: emote and badge images and emote lists from Twitch,
7TV (https://7tv.app), BetterTTV (https://betterttv.com) and FrankerFaceZ (https://www.frankerfacez.com) through their
public APIs and CDNs. The images belong to their creators; LiteView only shows them, like the services' own extensions.

LiteView for Twitch is an independent project. It is not affiliated with, endorsed by or sponsored
by Twitch Interactive, Inc. "Twitch" is a trademark of Twitch Interactive, Inc.
