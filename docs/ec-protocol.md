# EC protocol and recovery

The application uses the official signed `LpcACPIEC.bin` module from PawnIO.Modules 0.2.11 and PawnIO Official 2.2.0. Driver calls check returned byte counts and errors. Hardware addresses come from the selected `EcProfile`.

## Transactions

Current profiles communicate through ACPI EC data port 0x62 and command port 0x66. The ThinkPad transaction sequence waits for IBF to clear after sending the address, then reads the data. This follows the TPFanControl sequence and the T495 helper in the earlier implementation. An earlier L14 implementation waited for OBF and timed out on the author's machine.

Transactions acquire `Global\Access_EC` and `Global\Access_Thinkpad_EC` mutexes, with lock and port timeouts. These coordinate applications following the same protocol; firmware and Windows ACPI transactions are outside that coordination.

Fan writes are constrained to the profile's fan register, currently 0x2F, and values 1-7, 0x40, or 0x80. Level 0 and arbitrary register writes are not exposed. Every command is read back. BIOS and Max readback permit firmware level bits while requiring the correct mode flags.

## Sensors

CPU temperature at 0x78 is required for direct control. Temperatures through 0x7F are collected and the highest valid reading drives Smart auto and thermal protection. Other EC sensors are not labeled as CPU temperature. A battery reading cannot replace the CPU channel.

RPM reads the low byte at 0x84 before the high byte at 0x85, then combines them in little-endian order. Zero RPM can be valid in BIOS mode. 0xFFFF or implausible values are shown as unavailable. A temporarily missing RPM does not suppress valid temperature monitoring or protection.

## Recovery

The app attempts to restore BIOS on explicit selection, exit, suspend, logoff, lost sensors, invalid configuration, or a conflicting firmware mode change. Once connected, restoring BIOS does not require a successful temperature reading.

An independent process becomes ready before the first direct-control command. It watches completed-poll heartbeats, parent exit, and an emergency event. If armed without a heartbeat for 6.5 seconds, it sends 0x80, confirms readback, and retries failures. The main process does not kill an armed watchdog on exit.

User-mode recovery depends on a responding EC and driver. It cannot establish protection against every kernel hang or power loss. Logs record restoration only after confirmation. Hardware reports record executable hashes and device identity for each test run; they are separate from the profile list.

## References

- [ThinkPad ACPI Extras Driver](https://docs.kernel.org/admin-guide/laptops/thinkpad-acpi.html): levels, full-speed mode, firmware control, and RPM limitations.
- [Linux thinkpad_acpi.c](https://github.com/torvalds/linux/blob/master/drivers/platform/x86/lenovo/thinkpad_acpi.c): register references. The C# implementation is independently written.
- [TPFanCtrl2 portio.cpp](https://github.com/Shuzhengz/TPFanCtrl2/blob/main/fancontrol/portio.cpp): ThinkPad IBF wait sequence.
- [PawnIO Official](https://pawnio.eu/) and [PawnIO.Modules 0.2.11](https://github.com/namazso/PawnIO.Modules/releases/tag/0.2.11).
- [Earlier T495 configuration](https://github.com/NhanAZ-Tools/only-fans-control/blob/v1.0.2/only_fans_config.json) and [helper](https://github.com/NhanAZ-Tools/only-fans-control/blob/v1.0.2/helper/tvic_ec_helper.go).
