# Device profiles

`src/EcProfiles.cs` defines the hardware profiles. `DeviceProfile.Detect()` reads Windows device identity and selects a profile automatically.

| Profile | Manufacturer | Product name | Machine type | CPU |
| --- | --- | --- | --- | --- |
| `thinkpad-l14-gen4-amd` | LENOVO | ThinkPad L14 Gen 4 | 21H6 | AMD Ryzen, containing 7530U |
| `thinkpad-t495` | LENOVO | ThinkPad T495 | 20NK | AMD Ryzen |

All identity conditions must match. Similar names such as T495s, a different manufacturer, or an unlisted machine type do not select a profile. The backend checks the match again before opening PawnIO. BIOS versions are recorded in diagnostics rather than used as a model status label.

Both profiles currently use these protocol parameters:

| Parameter | Value |
| --- | --- |
| Data / command ports | 0x62 / 0x66 |
| Fan register | 0x2F |
| CPU temperature register | 0x78 |
| Temperature range | 0x78-0x7F |
| RPM low / high registers | 0x84 / 0x85 |
| BIOS / Max commands | 0x80 / 0x40 |

The T495 parameters and its initial curve come from this repository's earlier implementation, including the explicit type2 port selection. The current implementation uses PawnIO for both profiles. Hardware reports describe the specific device and executable used in each run.

The shared `curve` remains the default for existing configurations. A key in `profile_curves` overrides it for that profile only. Every curve is validated, including curves for models other than the connected device.

To add a model, add its immutable protocol description and identity conditions in `EcProfiles.cs`, provide a curve when needed, and add regression coverage for selection and rejection. Hardware addresses cannot be supplied through the user's JSON. Keep each profile narrow enough to avoid matching a different laptop accidentally.
