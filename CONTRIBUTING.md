# Contributing

Only Fans Control is built primarily for personal use. Issues and pull requests are welcome. Maintenance follows the author's needs and available time.

For an issue, include the application version, Windows version, ThinkPad model and machine type, BIOS version, selected mode, and the exact behavior or error. The EC connection dialog and `--diagnose` can provide useful details. Include relevant logs or readings when available and remove personal information before posting them.

For a pull request, explain the problem and resulting behavior, keep the change focused, and run `scripts/verify.ps1`. UI changes should include a screenshot. Hardware changes should explain their register and protocol references, include profile-selection tests, and provide hardware observations when available. Be explicit about which checks you actually ran.

Profiles live in `src/EcProfiles.cs`; fan policy lives in `src/FanController.cs`. Keep fan-register writes constrained, confirm commands by readback, and preserve BIOS recovery and the independent watchdog. Do not expose arbitrary EC writes through the configuration file.

Build instructions and test commands are in the README and `docs/testing.md`. Do not commit build outputs, downloaded drivers, logs, or private machine reports. Original icon sources, provenance, and licenses are tracked under `src/Assets/Lucide`.
