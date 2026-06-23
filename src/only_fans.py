from __future__ import annotations

import ctypes
import json
import logging
import os
import subprocess
import sys
import threading
import time
import tkinter as tk
import winreg
from dataclasses import dataclass
from ctypes import wintypes
from pathlib import Path
from tkinter import messagebox, ttk
from typing import Any

try:
    import pystray
    from PIL import Image
except Exception:
    pystray = None
    Image = None


APP_NAME = "Only Fans Control"
BASE_WINDOW_WIDTH = 600
BASE_WINDOW_HEIGHT = 520
STARTUP_VALUE_NAME = "OnlyFansControl"
STARTUP_REGISTRY_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"
INSTANCE_MUTEX_NAME = r"Local\NhanAZTools.OnlyFansControl.Mutex"
INSTANCE_EVENT_NAME = r"Local\NhanAZTools.OnlyFansControl.Activate"
ERROR_ALREADY_EXISTS = 183
WAIT_OBJECT_0 = 0


class SingleInstance:
    """Own the app-wide mutex and receive restore requests from later launches."""

    def __init__(
        self,
        mutex_name: str = INSTANCE_MUTEX_NAME,
        event_name: str = INSTANCE_EVENT_NAME,
    ) -> None:
        self.kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel32.CreateMutexW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.LPCWSTR]
        self.kernel32.CreateMutexW.restype = wintypes.HANDLE
        self.kernel32.CreateEventW.argtypes = [wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR]
        self.kernel32.CreateEventW.restype = wintypes.HANDLE
        self.kernel32.SetEvent.argtypes = [wintypes.HANDLE]
        self.kernel32.SetEvent.restype = wintypes.BOOL
        self.kernel32.ResetEvent.argtypes = [wintypes.HANDLE]
        self.kernel32.ResetEvent.restype = wintypes.BOOL
        self.kernel32.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
        self.kernel32.WaitForSingleObject.restype = wintypes.DWORD
        self.kernel32.CloseHandle.argtypes = [wintypes.HANDLE]
        self.kernel32.CloseHandle.restype = wintypes.BOOL

        ctypes.set_last_error(0)
        self.mutex_handle = self.kernel32.CreateMutexW(None, False, mutex_name)
        if not self.mutex_handle:
            raise ctypes.WinError(ctypes.get_last_error())
        self.is_primary = ctypes.get_last_error() != ERROR_ALREADY_EXISTS

        self.event_handle = self.kernel32.CreateEventW(None, True, False, event_name)
        if not self.event_handle:
            self.close()
            raise ctypes.WinError(ctypes.get_last_error())

        if not self.is_primary:
            self.kernel32.SetEvent(self.event_handle)
            self.close()

    def activation_requested(self) -> bool:
        if not self.event_handle:
            return False
        if self.kernel32.WaitForSingleObject(self.event_handle, 0) != WAIT_OBJECT_0:
            return False
        self.kernel32.ResetEvent(self.event_handle)
        return True

    def close(self) -> None:
        for attribute in ("event_handle", "mutex_handle"):
            handle = getattr(self, attribute, None)
            if handle:
                self.kernel32.CloseHandle(handle)
                setattr(self, attribute, None)


def app_root() -> Path:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parents[1]


ROOT = app_root()
BUNDLE_ROOT = Path(getattr(sys, "_MEIPASS", ROOT))
CONFIG_PATH = ROOT / "only_fans_config.json"
if not CONFIG_PATH.exists():
    CONFIG_PATH = BUNDLE_ROOT / "only_fans_config.json"
DRIVER_DIR = ROOT / "drivers"
LOG_DIR = ROOT / "logs"


def bundled_path(relative_path: str) -> Path:
    root_path = ROOT / relative_path
    if root_path.exists():
        return root_path
    return BUNDLE_ROOT / relative_path


HELPER_PATH = bundled_path("helper/tvic-ec-helper.exe")


def setup_logging() -> None:
    LOG_DIR.mkdir(exist_ok=True)
    logging.basicConfig(
        filename=LOG_DIR / "only-fans-control.log",
        level=logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
        encoding="utf-8",
        force=True,
    )


def parse_hex(value: str | int) -> int:
    if isinstance(value, int):
        return value
    return int(value, 16)


def is_admin() -> bool:
    try:
        return bool(ctypes.windll.shell32.IsUserAnAdmin())
    except Exception:
        return False


def relaunch_as_admin() -> None:
    script = str(Path(__file__).resolve())
    cwd = str(ROOT)
    params = f'"{script}"'
    rc = ctypes.windll.shell32.ShellExecuteW(None, "runas", sys.executable, params, cwd, 1)
    if rc <= 32:
        raise RuntimeError(f"ShellExecuteW failed: {rc}")
    sys.exit(0)


def run_powershell_json(command: str, timeout: int = 8) -> Any:
    full_command = f"$result = {command}; $result | ConvertTo-Json -Compress"
    completed = subprocess.run(
        ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", full_command],
        cwd=ROOT,
        capture_output=True,
        text=True,
        timeout=timeout,
        creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
    )
    if completed.returncode != 0 or not completed.stdout.strip():
        return None
    try:
        return json.loads(completed.stdout)
    except json.JSONDecodeError:
        return None


@dataclass(frozen=True)
class MachineInfo:
    manufacturer: str = "Unknown"
    model_code: str = "Unknown"
    marketing_name: str = "Unknown"
    bios_version: str = "Unknown"

    @property
    def display_name(self) -> str:
        return f"{self.manufacturer} {self.marketing_name} ({self.model_code})"


def detect_machine() -> MachineInfo:
    system = run_powershell_json(
        "[pscustomobject]@{"
        "Manufacturer=(Get-CimInstance Win32_ComputerSystem).Manufacturer;"
        "Model=(Get-CimInstance Win32_ComputerSystem).Model;"
        "Name=(Get-CimInstance Win32_ComputerSystemProduct).Version;"
        "Bios=(Get-CimInstance Win32_BIOS).SMBIOSBIOSVersion"
        "}"
    )
    if not isinstance(system, dict):
        return MachineInfo()
    return MachineInfo(
        manufacturer=str(system.get("Manufacturer") or "Unknown"),
        model_code=str(system.get("Model") or "Unknown"),
        marketing_name=str(system.get("Name") or "Unknown"),
        bios_version=str(system.get("Bios") or "Unknown"),
    )


@dataclass(frozen=True)
class SmartPoint:
    temp_c: int
    level: int


@dataclass
class AppConfig:
    owner_profile: str
    target_manufacturer: str
    target_model_code: str
    target_marketing_name: str
    target_bios_prefix: str
    command_port: int
    data_port: int
    preferred_backend: str
    tvic_port_type: str
    fan_control_register: int
    fan_rpm_lsb_register: int
    fan_rpm_msb_register: int
    bios_auto_value: int
    fan_max_value: int
    manual_min_level: int
    manual_max_level: int
    startup_mode: str
    poll_ms: int
    hysteresis_c: int
    failsafe_temp_c: int
    min_valid_temp_c: int
    max_valid_temp_c: int
    smart_curve: list[SmartPoint]

    @classmethod
    def load(cls) -> "AppConfig":
        data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        target = data["target_machine"]
        ec = data["ec"]
        policy = data["fan_policy"]
        return cls(
            owner_profile=data["owner_profile"],
            target_manufacturer=target["manufacturer"],
            target_model_code=target["model_code"],
            target_marketing_name=target["marketing_name"],
            target_bios_prefix=target["bios_prefix"],
            preferred_backend=str(ec.get("preferred_backend", "tvic-helper")),
            tvic_port_type=str(ec.get("tvic_port_type", "auto")),
            command_port=parse_hex(ec["command_port_hex"]),
            data_port=parse_hex(ec["data_port_hex"]),
            fan_control_register=parse_hex(ec["fan_control_register_hex"]),
            fan_rpm_lsb_register=parse_hex(ec["fan_rpm_lsb_register_hex"]),
            fan_rpm_msb_register=parse_hex(ec["fan_rpm_msb_register_hex"]),
            bios_auto_value=parse_hex(ec["bios_auto_value_hex"]),
            fan_max_value=parse_hex(ec.get("fan_max_value_hex", "0x40")),
            manual_min_level=int(policy["manual_min_level"]),
            manual_max_level=int(policy["manual_max_level"]),
            startup_mode=str(policy["startup_mode"]),
            poll_ms=int(policy["poll_ms"]),
            hysteresis_c=int(policy["hysteresis_c"]),
            failsafe_temp_c=int(policy["failsafe_temp_c"]),
            min_valid_temp_c=int(policy.get("min_valid_temp_c", 20)),
            max_valid_temp_c=int(policy.get("max_valid_temp_c", 105)),
            smart_curve=sorted(
                [SmartPoint(int(p["temp_c"]), int(p["level"])) for p in policy["smart_curve"]],
                key=lambda point: point.temp_c,
            ),
        )

    def matches(self, machine: MachineInfo) -> bool:
        manufacturer_ok = self.target_manufacturer.lower() in machine.manufacturer.lower()
        model_ok = machine.model_code.upper() == self.target_model_code.upper()
        name_ok = self.target_marketing_name.lower() in machine.marketing_name.lower()
        return manufacturer_ok and (model_ok or name_ok)

    @property
    def manual_max_step(self) -> int:
        return self.manual_max_level + 1

    def manual_raw_value(self, step: int) -> int:
        if step == self.manual_max_step or step == self.fan_max_value:
            return self.fan_max_value
        if self.manual_min_level <= step <= self.manual_max_level:
            return step
        raise ValueError(f"Manual step must be {self.manual_min_level}..{self.manual_max_step}")

    def manual_step_from_raw(self, raw_value: int | None) -> int | None:
        if raw_value is None:
            return None
        if raw_value == self.fan_max_value:
            return self.manual_max_step
        if self.manual_min_level <= raw_value <= self.manual_max_level:
            return raw_value
        return None

    def manual_label(self, step: int) -> str:
        if step == self.manual_max_step or step == self.fan_max_value:
            return "Max"
        return f"Level {step}"

    def manual_description(self, step: int) -> str:
        descriptions = {
            1: "Quietest manual step, best when the laptop is already cool.",
            2: "Light airflow with lower noise priority.",
            3: "Balanced low setting for normal daily work.",
            4: "Medium airflow when the laptop starts warming up.",
            5: "High airflow for longer heavy workloads.",
            6: "Very high airflow with cooling priority.",
            7: "Highest standard EC-controlled fan step.",
        }
        if step == self.manual_max_step or step == self.fan_max_value:
            return "Max: full-speed raw 0x40, louder and intended for quick cooling."
        return descriptions.get(step, f"Fan {self.manual_label(step)}.")


class PortIoError(RuntimeError):
    pass


class PortIo:
    name = "Unavailable"

    def read_byte(self, port: int) -> int:
        raise PortIoError("Port I/O backend is not available")

    def write_byte(self, port: int, value: int) -> None:
        raise PortIoError("Port I/O backend is not available")

    def close(self) -> None:
        return None


class WinRing0PortIo(PortIo):
    name = "WinRing0"

    def __init__(self, dll_path: Path) -> None:
        self.dll = ctypes.WinDLL(str(dll_path))
        self.dll.InitializeOls.restype = ctypes.c_bool
        self.dll.DeinitializeOls.restype = None
        self.dll.GetDllStatus.restype = ctypes.c_uint32
        self.dll.ReadIoPortByte.argtypes = [ctypes.c_ushort]
        self.dll.ReadIoPortByte.restype = ctypes.c_ubyte
        self.dll.WriteIoPortByte.argtypes = [ctypes.c_ushort, ctypes.c_ubyte]
        self.dll.WriteIoPortByte.restype = None
        if not self.dll.InitializeOls():
            status = self.dll.GetDllStatus()
            raise PortIoError(f"WinRing0 InitializeOls failed, status={status}")

    def read_byte(self, port: int) -> int:
        return int(self.dll.ReadIoPortByte(port)) & 0xFF

    def write_byte(self, port: int, value: int) -> None:
        self.dll.WriteIoPortByte(port, value & 0xFF)

    def close(self) -> None:
        try:
            self.dll.DeinitializeOls()
        except Exception:
            logging.exception("Failed to deinitialize WinRing0")


class InpOutPortIo(PortIo):
    name = "InpOutx64"

    def __init__(self, dll_path: Path) -> None:
        self.dll = ctypes.WinDLL(str(dll_path))
        self.dll.Inp32.argtypes = [ctypes.c_short]
        self.dll.Inp32.restype = ctypes.c_short
        self.dll.Out32.argtypes = [ctypes.c_short, ctypes.c_short]
        self.dll.Out32.restype = None
        if hasattr(self.dll, "IsInpOutDriverOpen"):
            self.dll.IsInpOutDriverOpen.restype = ctypes.c_bool
            if not self.dll.IsInpOutDriverOpen():
                raise PortIoError("InpOut driver is not open")

    def read_byte(self, port: int) -> int:
        return int(self.dll.Inp32(port)) & 0xFF

    def write_byte(self, port: int, value: int) -> None:
        self.dll.Out32(port, value & 0xFF)


def discover_port_io() -> tuple[PortIo | None, str]:
    DRIVER_DIR.mkdir(exist_ok=True)
    candidates = [
        (WinRing0PortIo, DRIVER_DIR / "WinRing0x64.dll"),
        (InpOutPortIo, DRIVER_DIR / "inpoutx64.dll"),
    ]
    errors: list[str] = []
    for backend_type, dll_path in candidates:
        if not dll_path.exists():
            continue
        try:
            backend = backend_type(dll_path)
            return backend, backend.name
        except Exception as exc:
            errors.append(f"{backend_type.name}: {exc}")
            logging.exception("Failed to open %s", backend_type.name)
    if errors:
        return None, "; ".join(errors)
    return None, "No driver found in the drivers/ folder"


class TvicHelperEc:
    def __init__(self, helper_path: Path, config: AppConfig) -> None:
        self.helper_path = helper_path
        self.config = config
        self.last_snapshot: dict[str, Any] | None = None
        self.lock = threading.Lock()
        if not helper_path.exists():
            raise FileNotFoundError(helper_path)
        snap = self.snapshot()
        if not snap.get("ok"):
            raise RuntimeError(str(snap.get("error") or "TVic helper probe failed"))

    def _run(self, *args: str, timeout: int = 8) -> dict[str, Any]:
        command = [
            str(self.helper_path),
            "--type",
            self.config.tvic_port_type,
            *args,
        ]
        completed = subprocess.run(
            command,
            cwd=ROOT,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout,
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
        payload = completed.stdout.strip() or completed.stderr.strip()
        try:
            data = json.loads(payload) if payload else {}
        except json.JSONDecodeError as exc:
            raise RuntimeError(f"Invalid helper output: {payload}") from exc
        if completed.returncode != 0 and data.get("ok") is not False:
            data["ok"] = False
            data["error"] = completed.stderr.strip() or f"helper exited {completed.returncode}"
        return data

    def snapshot(self) -> dict[str, Any]:
        with self.lock:
            data = self._run("snapshot")
            if data.get("ok"):
                self.last_snapshot = data
            return data

    def read_fan_level_raw(self) -> int | None:
        data = self.last_snapshot or self.snapshot()
        value = data.get("fan_level_raw")
        return int(value) if isinstance(value, int) else None

    def set_bios_auto(self) -> None:
        with self.lock:
            data = self._run("bios")
            if not data.get("ok"):
                raise RuntimeError(str(data.get("error") or "failed to set BIOS auto"))
            self.last_snapshot = data

    def set_manual_level(self, level: int) -> None:
        raw_value = self.config.manual_raw_value(level)
        with self.lock:
            data = self._run("level", str(raw_value))
            if not data.get("ok"):
                raise RuntimeError(str(data.get("error") or f"failed to set fan level {level}"))
            self.last_snapshot = data

    def read_fan_rpm(self) -> int | None:
        data = self.last_snapshot or self.snapshot()
        value = data.get("fan_rpm")
        return int(value) if isinstance(value, int) else None

    def read_ec_temperatures(self) -> list[int]:
        data = self.snapshot()
        raw_values = data.get("temperatures")
        if not isinstance(raw_values, list):
            return []
        temps: list[int] = []
        for value in raw_values:
            if not isinstance(value, int):
                continue
            if self.config.min_valid_temp_c <= value <= self.config.max_valid_temp_c:
                temps.append(value)
        return temps


def discover_ec_backend(config: AppConfig, admin: bool) -> tuple[Any | None, str]:
    if config.preferred_backend == "tvic-helper" and HELPER_PATH.exists():
        try:
            backend = TvicHelperEc(HELPER_PATH, config)
            snap = backend.last_snapshot or {}
            port_type = snap.get("port_type") or config.tvic_port_type
            return backend, f"TVicPort helper ready ({port_type})"
        except Exception as exc:
            logging.exception("TVic helper backend failed")
            helper_error = f"TVicPort helper error: {exc}"
    else:
        helper_error = "helper/tvic-ec-helper.exe was not found"

    if not admin:
        return None, f"{helper_error}. Administrator is required for WinRing0/InpOut fallback."

    port_io, status = discover_port_io()
    if not port_io:
        return None, f"{helper_error}. {status}"
    return ThinkPadEc(port_io, config), f"{status} ready"


def run_diagnostics() -> int:
    setup_logging()
    config = AppConfig.load()
    machine = detect_machine()
    admin = is_admin()
    backend, backend_status = discover_ec_backend(config, admin)
    if isinstance(backend, ThinkPadEc):
        backend.port_io.close()

    report = {
        "app": APP_NAME,
        "root": str(ROOT),
        "config_path": str(CONFIG_PATH),
        "machine": {
            "manufacturer": machine.manufacturer,
            "model_code": machine.model_code,
            "marketing_name": machine.marketing_name,
            "bios_version": machine.bios_version,
        },
        "target_matches": config.matches(machine),
        "is_admin": admin,
        "backend_status": backend_status,
        "helper_path": str(HELPER_PATH),
        "helper_exists": HELPER_PATH.exists(),
        "driver_dir": str(DRIVER_DIR),
        "driver_files": sorted(path.name for path in DRIVER_DIR.glob("*") if path.is_file()),
    }
    output_path = ROOT / "diagnostics.json"
    output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    logging.info("Diagnostics written to %s", output_path)
    return 0 if report["target_matches"] else 2


def run_control_command(argv: list[str]) -> int:
    setup_logging()
    config = AppConfig.load()
    machine = detect_machine()
    report: dict[str, Any] = {
        "app": APP_NAME,
        "machine": {
            "manufacturer": machine.manufacturer,
            "model_code": machine.model_code,
            "marketing_name": machine.marketing_name,
            "bios_version": machine.bios_version,
        },
        "target_matches": config.matches(machine),
    }
    output_path = ROOT / "control-result.json"

    if not config.matches(machine):
        report.update({"ok": False, "error": "target machine does not match config"})
        output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return 2

    backend, status = discover_ec_backend(config, is_admin())
    report["backend_status"] = status
    if not backend:
        report.update({"ok": False, "error": status})
        output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return 1

    try:
        if "--snapshot" in argv:
            if isinstance(backend, TvicHelperEc):
                report.update(backend.snapshot())
            else:
                report.update(
                    {
                        "ok": True,
                        "fan_level_raw": backend.read_fan_level_raw(),
                        "fan_rpm": backend.read_fan_rpm(),
                        "temperatures": backend.read_ec_temperatures(),
                    }
                )
        elif "--bios" in argv:
            backend.set_bios_auto()
            time.sleep(0.5)
            report.update({"ok": True, "action": "bios"})
            if isinstance(backend, TvicHelperEc):
                report.update(backend.snapshot())
        elif "--set-level" in argv:
            index = argv.index("--set-level")
            level_arg = argv[index + 1]
            level = config.manual_max_step if level_arg.lower() == "max" else int(level_arg)
            backend.set_manual_level(level)
            time.sleep(0.5)
            report.update(
                {
                    "ok": True,
                    "action": "set-level",
                    "level": level,
                    "raw_value": config.manual_raw_value(level),
                    "label": config.manual_label(level),
                }
            )
            if isinstance(backend, TvicHelperEc):
                report.update(backend.snapshot())
        else:
            report.update({"ok": False, "error": "unknown control command"})
            output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
            return 2
    except Exception as exc:
        logging.exception("Control command failed")
        report.update({"ok": False, "error": str(exc)})
        output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
        return 1
    finally:
        if isinstance(backend, ThinkPadEc):
            backend.port_io.close()

    output_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    logging.info("Control result written to %s", output_path)
    return 0 if report.get("ok") else 1


class ThinkPadEc:
    EC_STATUS_OBF = 0x01
    EC_STATUS_IBF = 0x02
    EC_CMD_READ = 0x80
    EC_CMD_WRITE = 0x81

    def __init__(self, port_io: PortIo, config: AppConfig) -> None:
        self.port_io = port_io
        self.config = config
        self.lock = threading.Lock()

    def _wait_input_empty(self, timeout_s: float = 0.4) -> None:
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            status = self.port_io.read_byte(self.config.command_port)
            if not (status & self.EC_STATUS_IBF):
                return
            time.sleep(0.001)
        raise PortIoError("Timed out waiting for EC input buffer")

    def _wait_output_full(self, timeout_s: float = 0.4) -> None:
        deadline = time.monotonic() + timeout_s
        while time.monotonic() < deadline:
            status = self.port_io.read_byte(self.config.command_port)
            if status & self.EC_STATUS_OBF:
                return
            time.sleep(0.001)
        raise PortIoError("Timed out waiting for EC output buffer")

    def read_ec(self, register: int) -> int:
        with self.lock:
            self._wait_input_empty()
            self.port_io.write_byte(self.config.command_port, self.EC_CMD_READ)
            self._wait_input_empty()
            self.port_io.write_byte(self.config.data_port, register)
            self._wait_output_full()
            return self.port_io.read_byte(self.config.data_port)

    def write_ec(self, register: int, value: int) -> None:
        with self.lock:
            self._wait_input_empty()
            self.port_io.write_byte(self.config.command_port, self.EC_CMD_WRITE)
            self._wait_input_empty()
            self.port_io.write_byte(self.config.data_port, register)
            self._wait_input_empty()
            self.port_io.write_byte(self.config.data_port, value)
            self._wait_input_empty()

    def read_fan_level_raw(self) -> int | None:
        try:
            return self.read_ec(self.config.fan_control_register)
        except Exception:
            logging.exception("Failed to read fan level")
            return None

    def set_bios_auto(self) -> None:
        self.write_ec(self.config.fan_control_register, self.config.bios_auto_value)

    def set_manual_level(self, level: int) -> None:
        self.write_ec(self.config.fan_control_register, self.config.manual_raw_value(level))

    def read_fan_rpm(self) -> int | None:
        try:
            lsb = self.read_ec(self.config.fan_rpm_lsb_register)
            msb = self.read_ec(self.config.fan_rpm_msb_register)
        except Exception:
            logging.exception("Failed to read fan RPM")
            return None
        rpm = (msb << 8) | lsb
        if rpm in (0xFFFF, 0x0000):
            return None
        return rpm

    def read_ec_temperatures(self) -> list[int]:
        temps: list[int] = []
        for register in range(0x78, 0x80):
            try:
                value = self.read_ec(register)
            except Exception:
                logging.exception("Failed to read EC temperature register 0x%02X", register)
                continue
            if 0 < value < 128:
                temps.append(value)
        return temps


class SmartPolicy:
    def __init__(self, config: AppConfig) -> None:
        self.config = config
        self.last_level: int | None = None

    def level_for(self, temp_c: int) -> int:
        desired = self.config.manual_min_level
        threshold_for_level = 0
        for point in self.config.smart_curve:
            if temp_c >= point.temp_c:
                desired = point.level
                threshold_for_level = point.temp_c

        if self.last_level is not None and desired < self.last_level:
            old_threshold = threshold_for_level
            for point in self.config.smart_curve:
                if point.level == self.last_level:
                    old_threshold = point.temp_c
            if temp_c > old_threshold - self.config.hysteresis_c:
                desired = self.last_level

        desired = max(self.config.manual_min_level, min(self.config.manual_max_level, desired))
        self.last_level = desired
        return desired


class OnlyFansApp:
    def __init__(self, single_instance: SingleInstance | None = None) -> None:
        setup_logging()
        self.config = AppConfig.load()
        self.machine = detect_machine()
        self.admin = is_admin()
        self.port_io: PortIo | None = None
        self.ec: Any | None = None
        self.backend_status = ""
        self.smart_policy = SmartPolicy(self.config)
        self.last_applied: str = "none"
        self.active_mode: str = "none"
        self.last_temp_c: int | None = None
        self.closed = False
        self.exiting = False
        self.tray_icon = None
        self.started_at_login = "--startup" in sys.argv
        self.single_instance = single_instance

        self.root = tk.Tk()
        self.set_window_icon()
        self.root.title(APP_NAME)
        self.root.protocol("WM_DELETE_WINDOW", self.on_close)
        if self.started_at_login:
            self.root.withdraw()

        self.mode_var = tk.StringVar(value=self.config.startup_mode)
        self.manual_level_var = tk.IntVar(value=0)
        self.status_var = tk.StringVar(value="Starting...")
        self.temp_var = tk.StringVar(value="Temperature: --")
        self.rpm_var = tk.StringVar(value="Fan: --")
        self.level_var = tk.StringVar(value="EC: --")
        self.backend_var = tk.StringVar(value="Backend: --")
        self.mode_info_var = tk.StringVar(value="")
        self.manual_info_var = tk.StringVar(value="")
        self.startup_var = tk.BooleanVar(value=self.startup_enabled())

        self.build_ui()
        self.initialize_backend()
        self.apply_current_mode()
        self.fit_and_lock_window()
        self.root.after(250, self.tick)
        self.root.after(200, self.poll_instance_activation)
        if self.started_at_login:
            self.root.after(100, self.minimize_to_tray)

    def fit_and_lock_window(self) -> None:
        self.root.update_idletasks()
        width = max(BASE_WINDOW_WIDTH, self.root.winfo_reqwidth())
        height = max(BASE_WINDOW_HEIGHT, self.root.winfo_reqheight())
        self.root.geometry(f"{width}x{height}")
        self.root.minsize(width, height)
        self.root.maxsize(width, height)
        self.root.resizable(False, False)

    @staticmethod
    def startup_command() -> str:
        if getattr(sys, "frozen", False):
            args = [str(Path(sys.executable).resolve()), "--startup"]
        else:
            args = [str(Path(sys.executable).resolve()), str(Path(__file__).resolve()), "--startup"]
        return subprocess.list2cmdline(args)

    @staticmethod
    def startup_enabled() -> bool:
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, STARTUP_REGISTRY_PATH) as key:
                command, _ = winreg.QueryValueEx(key, STARTUP_VALUE_NAME)
            return str(command).casefold() == OnlyFansApp.startup_command().casefold()
        except OSError:
            return False

    def set_startup_enabled(self, enabled: bool) -> None:
        with winreg.CreateKeyEx(
            winreg.HKEY_CURRENT_USER,
            STARTUP_REGISTRY_PATH,
            0,
            winreg.KEY_SET_VALUE,
        ) as key:
            if enabled:
                winreg.SetValueEx(
                    key,
                    STARTUP_VALUE_NAME,
                    0,
                    winreg.REG_SZ,
                    self.startup_command(),
                )
            else:
                try:
                    winreg.DeleteValue(key, STARTUP_VALUE_NAME)
                except FileNotFoundError:
                    pass

    def set_window_icon(self) -> None:
        icon_path = bundled_path("assets/fan.png")
        if not icon_path.exists():
            return
        try:
            self.icon_image = tk.PhotoImage(file=str(icon_path))
            self.root.iconphoto(True, self.icon_image)
            self.header_logo = self.icon_image.subsample(4, 4)
        except Exception:
            logging.exception("Failed to set window icon from %s", icon_path)

    def build_ui(self) -> None:
        style = ttk.Style()
        style.configure("Title.TLabel", font=("Segoe UI", 14, "bold"))
        style.configure("Metric.TLabel", font=("Segoe UI", 12))
        style.configure("Hint.TLabel", foreground="#4b5563")

        frame = ttk.Frame(self.root, padding=16)
        frame.pack(fill=tk.BOTH, expand=False)

        header = ttk.Frame(frame)
        header.pack(fill=tk.X, pady=(0, 12))
        if hasattr(self, "header_logo"):
            ttk.Label(header, image=self.header_logo).pack(side=tk.LEFT, padx=(0, 12))
        header_text = ttk.Frame(header)
        header_text.pack(side=tk.LEFT, fill=tk.X, expand=True)
        ttk.Label(header_text, text=APP_NAME, style="Title.TLabel").pack(anchor=tk.W)
        ttk.Label(header_text, text=f"{self.machine.display_name} | BIOS {self.machine.bios_version}").pack(anchor=tk.W, pady=(2, 0))

        metrics = ttk.Frame(frame)
        metrics.pack(fill=tk.X, pady=(0, 12))
        ttk.Label(metrics, textvariable=self.temp_var, style="Metric.TLabel").grid(row=0, column=0, sticky=tk.W, padx=(0, 20))
        ttk.Label(metrics, textvariable=self.rpm_var, style="Metric.TLabel").grid(row=0, column=1, sticky=tk.W, padx=(0, 20))
        ttk.Label(metrics, textvariable=self.level_var, style="Metric.TLabel").grid(row=0, column=2, sticky=tk.W)

        modes = ttk.LabelFrame(frame, text="Mode", padding=12)
        modes.pack(fill=tk.X)
        self.mode_radios = []
        for text, value in [
            ("Custom", "manual"),
            ("BIOS default", "bios"),
            ("Smart auto", "smart"),
        ]:
            radio = ttk.Radiobutton(
                modes,
                text=text,
                value=value,
                variable=self.mode_var,
                command=self.on_mode_selected,
            )
            radio.pack(anchor=tk.W, pady=3)
            self.mode_radios.append(radio)
        ttk.Label(modes, textvariable=self.mode_info_var, style="Hint.TLabel", wraplength=530).pack(anchor=tk.W, pady=(8, 0))

        custom = ttk.Frame(frame)
        custom.pack(fill=tk.X, pady=(14, 4))
        ttk.Label(custom, text="Custom level").pack(anchor=tk.W)
        levels = ttk.Frame(custom)
        levels.pack(fill=tk.X, pady=(6, 6))
        self.level_buttons: list[tk.Radiobutton] = []
        for step in range(self.config.manual_min_level, self.config.manual_max_step + 1):
            column = step - self.config.manual_min_level
            levels.columnconfigure(column, weight=1, uniform="levels")
            label_text = "Max" if step == self.config.manual_max_step else str(step)
            button = tk.Radiobutton(
                levels,
                text=label_text,
                variable=self.manual_level_var,
                value=step,
                indicatoron=False,
                width=7,
                padx=6,
                pady=8,
                relief=tk.RAISED,
                overrelief=tk.GROOVE,
                selectcolor="#dbeafe",
                command=self.on_level_selected,
            )
            button.grid(
                row=0,
                column=column,
                sticky=tk.EW,
                padx=3,
            )
            self.level_buttons.append(button)

        self.manual_label = ttk.Label(custom, textvariable=self.manual_info_var, style="Hint.TLabel", wraplength=530)
        self.manual_label.pack(anchor=tk.W)
        self.custom_controls = self.level_buttons

        buttons = ttk.Frame(frame)
        buttons.pack(fill=tk.X, pady=(12, 8))
        ttk.Button(buttons, text="BIOS now", command=self.force_bios).pack(side=tk.LEFT)
        ttk.Button(buttons, text="Run as admin", command=self.try_relaunch_admin).pack(side=tk.LEFT, padx=8)
        ttk.Button(buttons, text="Exit app", command=self.exit_app).pack(side=tk.RIGHT)
        ttk.Button(buttons, text="Minimize to tray", command=self.minimize_to_tray).pack(side=tk.RIGHT, padx=8)
        ttk.Checkbutton(
            buttons,
            text="Run at startup",
            variable=self.startup_var,
            command=self.on_startup_toggled,
        ).pack(side=tk.RIGHT, padx=8)

        curve = ", ".join(f"{p.temp_c}C -> L{p.level}" for p in self.config.smart_curve)
        ttk.Label(frame, text=f"Smart curve: {curve}", wraplength=530).pack(anchor=tk.W, pady=(8, 2))
        ttk.Label(frame, textvariable=self.backend_var, wraplength=530).pack(anchor=tk.W)
        ttk.Label(frame, textvariable=self.status_var, wraplength=530).pack(anchor=tk.W, pady=(8, 0))
        self.update_manual_info()
        self.update_mode_controls()

    def initialize_backend(self) -> None:
        supported = self.config.matches(self.machine)
        logging.info("Machine detected: %s", self.machine)
        logging.info("Target match: %s; admin: %s", supported, self.admin)
        if not supported:
            self.backend_status = "Machine does not match the ThinkPad T495/20NKS02N00 target config; fan control is locked."
            self.backend_var.set(f"Backend: {self.backend_status}")
            logging.warning("Unsupported machine: %s", self.machine)
            return
        self.ec, status = discover_ec_backend(self.config, self.admin)
        if isinstance(self.ec, ThinkPadEc):
            self.port_io = self.ec.port_io
        if not self.ec:
            self.backend_status = status
            self.backend_var.set(f"Backend: {self.backend_status}")
            logging.info("Backend unavailable: %s", self.backend_status)
            return
        self.backend_status = status
        self.backend_var.set(f"Backend: {self.backend_status}")
        logging.info("Backend ready: %s", status)

    def controls_ready(self) -> bool:
        return self.ec is not None

    def update_manual_info(self) -> None:
        step = self.manual_level_var.get()
        if step == 0:
            if self.mode_var.get() == "manual":
                self.manual_info_var.set("Choose a custom fan level. The selected level is applied immediately.")
            else:
                self.manual_info_var.set("Custom levels are available only in Custom mode.")
            return
        label = self.config.manual_label(step)
        raw_value = self.config.manual_raw_value(step)
        self.manual_info_var.set(f"{label} | raw 0x{raw_value:02X} | {self.config.manual_description(step)}")

    def update_mode_controls(self) -> None:
        mode = self.mode_var.get()
        manual_enabled = mode == "manual" and self.controls_ready()
        if mode != "manual" and self.manual_level_var.get() != 0:
            self.manual_level_var.set(0)
        for control in getattr(self, "custom_controls", []):
            control.configure(state=tk.NORMAL if manual_enabled else tk.DISABLED)

        mode_text = {
            "manual": "Custom: choose one discrete level below. The selection is applied immediately.",
            "bios": "BIOS default: the laptop firmware/EC controls the fan automatically.",
            "smart": "Smart auto: the app adjusts the fan level from temperature using the curve below.",
        }.get(mode, "")
        self.mode_info_var.set(mode_text)
        self.update_manual_info()

    def on_mode_selected(self) -> None:
        self.update_mode_controls()
        self.apply_current_mode()

    def on_level_selected(self) -> None:
        self.update_manual_info()
        if self.mode_var.get() != "manual":
            return
        self.apply_current_mode()

    def try_relaunch_admin(self) -> None:
        if self.admin:
            messagebox.showinfo(APP_NAME, "The app is already running as Administrator.")
            return
        try:
            relaunch_as_admin()
        except Exception as exc:
            messagebox.showerror(APP_NAME, f"Could not relaunch as Administrator: {exc}")

    def on_startup_toggled(self) -> None:
        enabled = self.startup_var.get()
        try:
            self.set_startup_enabled(enabled)
            state = "enabled" if enabled else "disabled"
            self.set_status(f"Run at startup is {state}.")
        except OSError as exc:
            self.startup_var.set(not enabled)
            self.set_status(f"Could not update Run at startup: {exc}")

    def set_status(self, text: str) -> None:
        self.status_var.set(text)
        logging.info(text)

    def force_bios(self) -> None:
        self.mode_var.set("bios")
        self.apply_current_mode()

    def apply_current_mode(self) -> None:
        mode = self.mode_var.get()
        self.update_mode_controls()
        if not self.controls_ready():
            self.set_status("Fan control is not ready. Check the TVicPort backend or drivers/ fallback files.")
            return

        assert self.ec is not None
        try:
            if mode == "bios":
                self.ec.set_bios_auto()
                self.last_applied = "bios"
                self.active_mode = "bios"
                self.set_status("Fan control returned to BIOS default.")
            elif mode == "manual":
                level = self.manual_level_var.get()
                self.active_mode = "manual"
                if level == 0:
                    self.set_status("Custom mode is selected. Choose a fan level to apply it.")
                    return
                self.ec.set_manual_level(level)
                self.last_applied = f"manual:{level}"
                raw_value = self.config.manual_raw_value(level)
                self.set_status(f"Fan set to {self.config.manual_label(level)} (raw 0x{raw_value:02X}).")
            elif mode == "smart":
                self.manual_level_var.set(0)
                self.update_manual_info()
                self.active_mode = "smart"
                self.apply_smart_once()
            else:
                self.set_status(f"Invalid mode: {mode}")
        except Exception as exc:
            logging.exception("Failed to apply mode")
            self.set_status(f"Failed to apply mode: {exc}")

    def apply_smart_once(self) -> None:
        if not self.ec:
            return
        if self.last_temp_c is None:
            self.set_status("Smart auto is waiting for an EC temperature reading.")
            return
        if self.last_temp_c >= self.config.failsafe_temp_c:
            self.ec.set_bios_auto()
            self.mode_var.set("bios")
            self.manual_level_var.set(0)
            self.last_applied = "bios:failsafe"
            self.active_mode = "bios"
            self.update_mode_controls()
            self.set_status(f"Temperature {self.last_temp_c}C hit the failsafe threshold; fan control returned to BIOS default.")
            return
        level = self.smart_policy.level_for(self.last_temp_c)
        marker = f"smart:{level}"
        if marker != self.last_applied:
            self.ec.set_manual_level(level)
            self.last_applied = marker
            self.set_status(f"Smart auto: {self.last_temp_c}C -> fan level {level}.")

    def tick(self) -> None:
        if self.closed:
            return
        try:
            self.refresh_sensors()
            if self.active_mode == "smart" and self.controls_ready():
                self.apply_smart_once()
        except Exception:
            logging.exception("Tick failed")
        self.root.after(self.config.poll_ms, self.tick)

    def poll_instance_activation(self) -> None:
        if self.closed:
            return
        if self.single_instance and self.single_instance.activation_requested():
            self.show_window()
            self.set_status("The existing app window was restored instead of opening a second instance.")
        self.root.after(200, self.poll_instance_activation)

    def refresh_sensors(self) -> None:
        rpm = None
        raw_level = None
        temps: list[int] = []
        if isinstance(self.ec, TvicHelperEc):
            data = self.ec.snapshot()
            if data.get("ok"):
                value = data.get("fan_rpm")
                rpm = int(value) if isinstance(value, int) else None
                raw = data.get("fan_level_raw")
                raw_level = int(raw) if isinstance(raw, int) else None
                raw_temps = data.get("temperatures")
                if isinstance(raw_temps, list):
                    temps = [
                        value
                        for value in raw_temps
                        if isinstance(value, int)
                        and self.config.min_valid_temp_c <= value <= self.config.max_valid_temp_c
                    ]
        elif self.ec:
            rpm = self.ec.read_fan_rpm()
            raw_level = self.ec.read_fan_level_raw()
            temps = self.ec.read_ec_temperatures()

        if temps:
            self.last_temp_c = max(temps)
            temp_detail = "/".join(str(t) for t in temps)
            self.temp_var.set(f"Temperature: {self.last_temp_c}C ({temp_detail})")
        else:
            self.last_temp_c = None
            self.temp_var.set("Temperature: --")

        self.rpm_var.set(f"Fan: {rpm} RPM" if rpm is not None else "Fan: --")
        if raw_level is None:
            self.level_var.set("EC: --")
        elif raw_level & 0x80:
            self.level_var.set(f"EC: BIOS 0x{raw_level:02X}")
        elif raw_level == self.config.fan_max_value:
            self.level_var.set(f"EC: MAX 0x{raw_level:02X}")
        else:
            self.level_var.set(f"EC: L{raw_level & 0x07} 0x{raw_level:02X}")

    def on_close(self) -> None:
        self.minimize_to_tray()

    def minimize_to_tray(self) -> None:
        if self.exiting:
            return
        if pystray is None or Image is None:
            self.root.iconify()
            self.set_status("Tray backend is unavailable; the window was minimized.")
            return
        try:
            if self.tray_icon is None:
                icon_path = bundled_path("assets/fan.png")
                image = Image.open(icon_path)
                menu = pystray.Menu(
                    pystray.MenuItem(f"Open {APP_NAME}", self.tray_show_window, default=True),
                    pystray.MenuItem("Return to BIOS default", self.tray_force_bios),
                    pystray.MenuItem("Exit app", self.tray_exit_app),
                )
                self.tray_icon = pystray.Icon(APP_NAME, image, APP_NAME, menu)
            self.tray_icon.run_detached()
            self.root.withdraw()
            self.set_status("Running in the system tray.")
        except Exception:
            logging.exception("Failed to minimize to tray")
            self.root.iconify()

    def tray_show_window(self, icon: Any = None, item: Any = None) -> None:
        self.root.after(0, self.show_window)

    def show_window(self) -> None:
        self.root.deiconify()
        self.root.lift()
        self.root.focus_force()

    def tray_force_bios(self, icon: Any = None, item: Any = None) -> None:
        self.root.after(0, self.force_bios)

    def tray_exit_app(self, icon: Any = None, item: Any = None) -> None:
        self.root.after(0, self.exit_app)

    def exit_app(self) -> None:
        if self.exiting:
            return
        self.exiting = True
        self.closed = True
        logging.info("Closing app")
        if self.ec:
            try:
                self.ec.set_bios_auto()
                logging.info("Returned fan to BIOS auto on exit")
            except Exception:
                logging.exception("Failed to return fan to BIOS auto on exit")
        if isinstance(self.ec, ThinkPadEc):
            self.ec.port_io.close()
        elif self.port_io:
            self.port_io.close()
        if self.tray_icon is not None:
            try:
                self.tray_icon.stop()
            except Exception:
                logging.exception("Failed to stop tray icon")
        if self.single_instance:
            self.single_instance.close()
        self.root.destroy()

    def run(self) -> None:
        self.root.mainloop()


def main() -> None:
    single_instance = None
    try:
        if "--diagnose" in sys.argv:
            raise SystemExit(run_diagnostics())
        if any(arg in sys.argv for arg in ["--snapshot", "--bios", "--set-level"]):
            raise SystemExit(run_control_command(sys.argv[1:]))
        single_instance = SingleInstance()
        if not single_instance.is_primary:
            return
        app = OnlyFansApp(single_instance)
        app.run()
    except Exception as exc:
        setup_logging()
        logging.exception("Fatal startup error")
        try:
            messagebox.showerror(APP_NAME, f"Startup error:\n{exc}")
        except Exception:
            pass
        raise
    finally:
        if single_instance:
            single_instance.close()


if __name__ == "__main__":
    main()
