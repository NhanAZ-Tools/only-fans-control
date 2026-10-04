# Only Fans Control

A minimal Windows fan monitor and controller for selected Lenovo ThinkPad models. Built primarily for personal use. Issues and pull requests are welcome.

The native C#/WPF app has an English interface, blue Light mode, [Lucide icons](https://lucide.dev/) and the [Fan logo](https://lucide.dev/icons/fan). It runs in the system tray. Mode and level changes apply immediately, with no Apply button.

## Device profiles

| Device | Machine type | CPU match | Profile |
| --- | --- | --- | --- |
| Lenovo ThinkPad L14 Gen 4 AMD | `21H6` | AMD Ryzen 7530U | `thinkpad-l14-gen4-amd` |
| Lenovo ThinkPad T495 | `20NK` | AMD Ryzen | `thinkpad-t495` |

The app selects a profile automatically from manufacturer, machine type, product name, and CPU. Other devices receive system monitoring without EC fan control. Hardware addresses are defined in the application profiles; they are not editable in the fan-curve JSON. See [device profiles](docs/device-profiles.md).

## Run

Requires Windows 10/11 x64 and .NET Framework 4.8.

1. Extract `OnlyFansControl-Windows-x64.zip`, or use the installer.
2. Install the bundled `driver/PawnIO_setup.exe`, selecting the **Official** version.
3. Open `Start-OnlyFansControl.cmd` and accept the Windows UAC prompt.
4. Use **EC connection** for connection details, reconnection, and logs.

Running the EXE without administrator rights opens system monitoring. Direct EC temperature, RPM, and fan control require administrator rights and PawnIO. Driver installation is initiated by the user.

Closing or minimizing hides the app to the tray. Opening it again restores the existing window. Use **Exit · restore BIOS** to exit completely. Each launch starts by monitoring the current state; a previous manual mode is not reapplied. The `--startup` argument opens directly in the tray.

## Modes

| Mode | Behavior |
| --- | --- |
| Custom | Select a discrete level **1–7** or **Max**. Each click selects Custom and sends the command immediately. |
| Max | Send the full-speed EC command **0x40**. |
| BIOS default | Send **0x80** to return fan control to Lenovo firmware. |
| Smart auto | Use the highest valid EC temperature and the selected profile's JSON curve. |

Fan levels are EC steps, not fixed percentages or RPM targets. Firmware and settling time affect the resulting RPM.

## Configuration

**Open config** opens `only_fans_config.json` beside the EXE. Saved changes reload during the next polling cycle.

- `curve` supplies the shared curve, initially tuned for the L14.
- `profile_curves` optionally supplies a curve for a specific profile. The supplied configuration includes the T495 curve from the earlier implementation. Remove an override to use the shared curve.
- `poll_interval_ms` sets the polling interval, initially 2000 ms.
- `hysteresis_c` holds the current level during small cooling changes, initially 3 °C.
- `critical_temperature_c` forces Max while the app owns fan control, initially 90 °C. It releases below the threshold minus hysteresis. BIOS default leaves firmware in control.

Existing schema-version-1 configuration files without `profile_curves` continue to use their shared curve. Unknown profile keys, invalid curves, or invalid JSON disable Smart auto and restore any fan control held by the app to BIOS.

The earlier T495 `fan_policy` configuration is also accepted: its curve, polling interval, hysteresis, and temperature limit are read without changing the file. Its hardware-address fields do not override the application profiles. The installer preserves an existing configuration.

## EC behavior

The app uses the signed PawnIO `LpcACPIEC` module. Current profiles use ports `0x62/0x66`, fan register `0x2F`, CPU temperature at `0x78`, other temperatures through `0x7F`, and RPM from `0x84/0x85`.

Only levels 1–7, Max, and BIOS default may be written, with readback after every command. A CPU reading is required before control. Missing sensors and communication errors trigger BIOS recovery. An independent watchdog is ready before manual control begins and attempts BIOS recovery when the app exits, fails to send heartbeats for 6.5 seconds, or requests emergency recovery.

Hardware recovery can fail if the EC or driver stops responding; the watchdog retries. Logs are stored in `%LOCALAPPDATA%/OnlyFansControl/app.log`. There is no account, telemetry, or network requirement at runtime. See [EC protocol](docs/ec-protocol.md) for details and references.

## Build

The build uses the .NET Framework compiler included with Windows:

```powershell
.\scripts\restore-dependencies.ps1
.\build.ps1 -SkipPackage
.\scripts\verify.ps1
.\scripts\package.ps1
```

Output: `build/OnlyFansControl/OnlyFansControl.exe`. Packages and checksums are written to the repository root. Python, Go, and a .NET SDK are not required. The source package includes the original SVGs, complete third-party notices, and build scripts.

Build the installer with Inno Setup 6:

```powershell
.\build-installer.ps1
```

The Windows CI workflow builds the app, runs simulated-EC and process tests, renders the WPF interface, and builds the installer. Physical fan tests run separately on the target laptop. See [testing](docs/testing.md).

## Contributions and license

See [CONTRIBUTING.md](CONTRIBUTING.md) for fixes, profiles, and useful issue details. This project is maintained according to the author's personal needs and available time.

Application code is under the [MIT license](LICENSE). Distributed components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). The app is not affiliated with Lenovo.
