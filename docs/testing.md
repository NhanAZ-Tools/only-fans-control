# Testing

## Build and simulated tests

```powershell
.\scripts\restore-dependencies.ps1
.\build.ps1 -SkipPackage
.\scripts\verify.ps1
```

This checks controller logic with a simulated EC, profile matching and curve selection, configuration rejection, named single-instance activation, and watchdog recovery using separate processes. It also records diagnostics and renders the WPF interface. No real fan register is written.

## Hardware tests

Close any other fan controller before testing. The scripts request administrator rights when needed.

```powershell
.\scripts\verify-hardware.ps1 -ReadOnly
.\scripts\verify-hardware.ps1
.\build\OnlyFansControl\OnlyFansControl.exe --ui-test "$PWD\artifacts\ui-test.json"
.\scripts\verify-watchdog-crash.ps1
```

Run `--ui-test` from an elevated terminal. The hardware run changes fan modes, measures temperature and RPM, and restores BIOS control. At high temperatures it tests low-level commands briefly rather than holding them. The GUI test exercises modes, configuration reload and recovery, tray behavior, second-launch activation, and exit. It restores the original JSON bytes. The crash test terminates only its dedicated test process and checks the watchdog's real BIOS restore.

Reports include the executable SHA-256 and hardware identity. They document individual runs, not model status labels. A simulated test or successful CI build does not measure physical RPM. Tests on one laptop do not establish behavior on another laptop.

For a release with hardware evidence, copy a final read-only probe to `artifacts/ec-probe-final.json`, run `scripts/write-verification-report.ps1`, and package with `scripts/package.ps1 -RequireVerified`. The package script requires successful hardware, GUI, and crash reports that match the current executable. General builds can be packaged without hardware reports.
