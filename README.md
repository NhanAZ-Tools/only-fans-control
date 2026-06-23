# Only Fans Control

A minimal Windows fan monitor and controller tuned for NhanAZ's Lenovo ThinkPad T495.

Detected target machine:

- Manufacturer: `LENOVO`
- Model code: `20NKS02N00`
- Marketing name: `ThinkPad T495`
- BIOS: `R12ET64W(1.34)`

## Modes

- `Custom`: choose one discrete fan step from `1` to `7`, or `Max`.
- `Max`: the full-speed custom step, writing raw `0x40` to the Embedded Controller.
- `BIOS default`: return fan control to the laptop firmware/Embedded Controller.
- `Smart auto`: read EC temperatures and adjust the fan level from the curve in `only_fans_config.json`.

Changing the mode or custom level applies immediately. The app has no Apply button.

Closing the window with `X` minimizes the app to the system tray. To exit for real, use the tray menu `Exit`; the app will try to return fan control to BIOS default first.

## Run

```powershell
.\run.ps1
```

The script asks for Administrator rights when needed.

## Build the `.exe`

Install Python dependencies first:

```powershell
python -m pip install -r requirements.txt
```

The build also requires Go because the TVicPort EC helper is compiled from `helper\tvic_ec_helper.go`.

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

If `C:\Users\NhanAZ\Downloads\fan.svg` exists, the build script uses it as the app and executable icon. Otherwise it falls back to `assets\fan.svg`.

The build output is:

```text
dist\OnlyFansControl\OnlyFansControl.exe
```

Run the built executable as Administrator:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\run-exe-admin.ps1
```

Run diagnostics from the build:

```powershell
.\dist\OnlyFansControl\OnlyFansControl.exe --diagnose
```

The diagnostics report is written to `dist\OnlyFansControl\diagnostics.json`.

## Fan Control Backend

The ThinkPad T495 does not expose fan control through standard WMI. This app controls the ThinkPad Embedded Controller directly:

- fan control register: `0x2F`
- BIOS/default auto value: `0x80`
- max/full-speed value: `0x40`
- fan RPM registers: `0x84` + `0x85`

On this machine the preferred backend is `TVicPort` through the 32-bit helper at `helper\tvic-ec-helper.exe`. The installed `TVicPort64` driver was verified as running on the target Windows system.

If `TVicPort` is unavailable, the app can fall back to another port I/O driver. Place one of these sets in `drivers/` and run the app as Administrator:

- `WinRing0x64.dll` and `WinRing0x64.sys`
- or `inpoutx64.dll` and the matching driver

Without a working backend the app still opens, but monitoring and fan control stay locked.

## Safety

- Manual level `0` is intentionally not exposed to avoid turning the fan off by accident.
- If the temperature reaches `failsafe_temp_c`, Smart auto returns control to BIOS default.
- On real exit, the app tries to write `0x80` so the BIOS/EC controls the fan again.
- `Max` writes raw `0x40` and should be used only when you intentionally want full fan speed.

Fan control is low-level hardware behavior. Lenovo does not provide this as a normal application API. Watch temperatures during the first few minutes when using `Custom`, `Max`, or `Smart auto`.
