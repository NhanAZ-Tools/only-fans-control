# Changelog

## 2.0.0

- Replace the Python/Tkinter and Go helper implementation with a native C#/WPF Windows application using PawnIO Official.
- Select a hardware profile automatically for ThinkPad L14 Gen 4 AMD (21H6 / 7530U) and ThinkPad T495 (20NK / AMD Ryzen).
- Keep Custom 1-7/Max, Max, BIOS default, and Smart auto with immediate application.
- Provide a shared fan curve and an optional T495 curve. Accept earlier T495 configuration files and preserve existing configuration during installation.
- Add command readback, required CPU temperature checks, thermal protection in manual modes, and an independent BIOS-recovery watchdog.
- Use an English blue Light interface with embedded Lucide icons and the Fan logo.
- Restore the existing window on a second launch and accept startup-to-tray launches.
- Build and test on Windows without Python, Go, or a .NET SDK; provide portable packages, source, notices, and an installer.
- Document device profiles and contribution guidelines without model status labels.

Previous releases and their source remain available in the existing v1.0.0, v1.0.1, and v1.0.2 tags.
