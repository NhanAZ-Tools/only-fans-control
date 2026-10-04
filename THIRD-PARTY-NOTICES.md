# Third-party notices

## Lucide icons 1.52.0

Copyright (c) 2026 Lucide Icons and Contributors. ISC License. Icons derived from Feather also carry the MIT license and Copyright (c) 2013-present Cole Bemis. Both complete license texts are included in `assets/Lucide/LICENSE` and embedded as `Lucide.LICENSE` in the executable.

Source: https://github.com/lucide-icons/lucide/tree/1.52.0

Pinned source commit: `500620a2e8123f8d1db191538886dc0c223f69a9`.

The app uses these official SVGs: activity, battery, chart-line, circuit-board, cpu, download, fan, folder-open, laptop, minimize-2, plug, power, refresh-cw, settings-2, shield, shield-check, sliders-horizontal, thermometer, and zap.

The Fan SVG supplies the application logo: https://lucide.dev/icons/fan . Original artwork is preserved; colors and size are applied during rendering. The Windows ICO and PNG are generated from the same Fan SVG. Each SVG source URL and SHA-256 is recorded in `assets/Lucide/provenance.json`. Sources are included in the package, and the app uses a native WPF renderer instead of including a Lucide JavaScript or .NET package.

## LpcACPIEC.bin — PawnIO.Modules 0.2.11

Copyright namazso. LGPL-2.1-or-later. The signed module is embedded unchanged in `OnlyFansControl.exe`.

Source: https://github.com/namazso/PawnIO.Modules/tree/0.2.11

The source archive, `LpcACPIEC.p`, `pawnio.inc`, and `COPYING` license text are provided in `module-source/`. To replace the module, update `vendor/PawnIO.Modules-0.2.11/LpcACPIEC.bin` and rebuild the app with `scripts/build.ps1`. The Official driver verifies module signatures as designed by PawnIO.

Binary archive SHA-256:
`43608CB89BC84247FEF1368A139013F7D043E17DB6D6C8DFC9B46BF0905A81F4`

## PawnIO Official installer 2.2.0

The installer is distributed unchanged and is run by the user. Official download:
https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe

Driver: Copyright namazso, GPL-2.0-or-later with the device I/O interface exception. Complete source: https://github.com/namazso/PawnIO

The source archive for driver 2.2.0 (commit `5cdf470831fdfff3f7f1d06363ca6b230f3bf35a`), the corresponding PawnPP submodule, and `COPYING` license text are provided in `module-source/`. The PawnIO.Setup repository distributes the installer. Refer to the terms included in the installer and corresponding driver source.

The Authenticode signature was verified as valid when downloaded. Certificate subject: `namazso.eu`.

Installer SHA-256:
`1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`

The app communicates with the driver through DeviceIoControl. It does not use WinRing0, WinIO, or unsigned drivers.

## TPFanControl protocol reference

The EC input-buffer (IBF) wait sequence was checked against TPFanCtrl2's `fancontrol/portio.cpp`: https://github.com/Shuzhengz/TPFanCtrl2/blob/main/fancontrol/portio.cpp . The original file declares itself public domain. TVicPort DLLs, drivers, and installers are not distributed in the app package.
