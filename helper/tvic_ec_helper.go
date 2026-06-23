package main

import (
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"syscall"
	"time"
)

const (
	ecFlagOBF   = 0x01
	ecFlagIBF   = 0x02
	ecCmdRead   = 0x80
	ecCmdWrite  = 0x81
	fanRegister = 0x2f
	rpmRegister = 0x84
	temp0       = 0x78
	biosAuto    = 0x80
	fanMax      = 0x40
)

type ecPorts struct {
	Name string `json:"name"`
	Ctrl uint16 `json:"ctrl"`
	Data uint16 `json:"data"`
}

var portTypes = []ecPorts{
	{Name: "type1", Ctrl: 0x1604, Data: 0x1600},
	{Name: "type2", Ctrl: 0x66, Data: 0x62},
}

type tvicPort struct {
	dll            *syscall.LazyDLL
	openTVicPort   *syscall.LazyProc
	closeTVicPort  *syscall.LazyProc
	isDriverOpened *syscall.LazyProc
	readPort       *syscall.LazyProc
	writePort      *syscall.LazyProc
}

type snapshot struct {
	OK           bool   `json:"ok"`
	Backend      string `json:"backend"`
	PortType     string `json:"port_type"`
	FanLevelRaw  int    `json:"fan_level_raw"`
	FanRPM       *int   `json:"fan_rpm"`
	Temperatures []int  `json:"temperatures"`
	Error        string `json:"error,omitempty"`
}

func tvicDLLPath() string {
	if windir := os.Getenv("WINDIR"); windir != "" {
		return filepath.Join(windir, "system", "TVicPort.dll")
	}
	return `C:\Windows\system\TVicPort.dll`
}

func openTVic() (*tvicPort, error) {
	path := tvicDLLPath()
	if _, err := os.Stat(path); err != nil {
		return nil, fmt.Errorf("TVicPort.dll not found at %s: %w", path, err)
	}

	t := &tvicPort{
		dll: syscall.NewLazyDLL(path),
	}
	t.openTVicPort = t.dll.NewProc("OpenTVicPort")
	t.closeTVicPort = t.dll.NewProc("CloseTVicPort")
	t.isDriverOpened = t.dll.NewProc("IsDriverOpened")
	t.readPort = t.dll.NewProc("ReadPort")
	t.writePort = t.dll.NewProc("WritePort")

	if err := t.dll.Load(); err != nil {
		return nil, err
	}
	r, _, err := t.openTVicPort.Call()
	if r == 0 {
		return nil, fmt.Errorf("OpenTVicPort failed: %w", err)
	}
	r, _, _ = t.isDriverOpened.Call()
	if r == 0 {
		t.close()
		return nil, errors.New("TVicPort driver is not opened")
	}
	return t, nil
}

func (t *tvicPort) close() {
	if t != nil && t.closeTVicPort != nil {
		t.closeTVicPort.Call()
	}
}

func (t *tvicPort) inb(port uint16) byte {
	r, _, _ := t.readPort.Call(uintptr(port))
	return byte(r & 0xff)
}

func (t *tvicPort) outb(port uint16, value byte) {
	t.writePort.Call(uintptr(port), uintptr(value))
}

func (t *tvicPort) waitFlags(p ecPorts, flags byte, on bool, timeout time.Duration) bool {
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		data := t.inb(p.Ctrl)
		state := (data & flags) != 0
		if state == on {
			return true
		}
		time.Sleep(10 * time.Millisecond)
	}
	return false
}

func (t *tvicPort) readEC(p ecPorts, offset byte) (byte, error) {
	if !t.waitFlags(p, ecFlagIBF|ecFlagOBF, false, 1000*time.Millisecond) {
		return 0, fmt.Errorf("%s readec timeout #1", p.Name)
	}
	t.outb(p.Ctrl, ecCmdRead)
	if !t.waitFlags(p, ecFlagIBF, false, 1000*time.Millisecond) {
		return 0, fmt.Errorf("%s readec timeout #2", p.Name)
	}
	t.outb(p.Data, offset)
	if !t.waitFlags(p, ecFlagIBF, false, 1000*time.Millisecond) {
		return 0, fmt.Errorf("%s readec timeout #3", p.Name)
	}
	return t.inb(p.Data), nil
}

func (t *tvicPort) writeEC(p ecPorts, offset byte, value byte) error {
	if !t.waitFlags(p, ecFlagIBF|ecFlagOBF, false, 1000*time.Millisecond) {
		return fmt.Errorf("%s writeec timeout #1", p.Name)
	}
	t.outb(p.Ctrl, ecCmdWrite)
	if !t.waitFlags(p, ecFlagIBF, false, 1000*time.Millisecond) {
		return fmt.Errorf("%s writeec timeout #2", p.Name)
	}
	t.outb(p.Data, offset)
	if !t.waitFlags(p, ecFlagIBF, false, 1000*time.Millisecond) {
		return fmt.Errorf("%s writeec timeout #3", p.Name)
	}
	t.outb(p.Data, value)
	if !t.waitFlags(p, ecFlagIBF, false, 1000*time.Millisecond) {
		return fmt.Errorf("%s writeec timeout #4", p.Name)
	}
	return nil
}

func selectPorts(kind string) []ecPorts {
	switch strings.ToLower(kind) {
	case "1", "type1":
		return portTypes[:1]
	case "2", "type2":
		return portTypes[1:]
	default:
		return portTypes
	}
}

func readSnapshot(t *tvicPort, kinds []ecPorts) snapshot {
	var lastErr error
	for _, p := range kinds {
		level, err := t.readEC(p, fanRegister)
		if err != nil {
			lastErr = err
			continue
		}

		lo, loErr := t.readEC(p, rpmRegister)
		hi, hiErr := t.readEC(p, rpmRegister+1)
		var rpmPtr *int
		if loErr == nil && hiErr == nil {
			rpm := (int(hi) << 8) | int(lo)
			if rpm > 0 && rpm <= 0x1fff {
				rpmPtr = &rpm
			}
		}

		temps := make([]int, 0, 8)
		for i := 0; i < 8; i++ {
			value, err := t.readEC(p, temp0+byte(i))
			if err == nil && value > 0 && value < 128 {
				temps = append(temps, int(value))
			}
		}

		return snapshot{
			OK:           true,
			Backend:      "TVicPort",
			PortType:     p.Name,
			FanLevelRaw:  int(level),
			FanRPM:       rpmPtr,
			Temperatures: temps,
		}
	}
	errText := "EC read failed"
	if lastErr != nil {
		errText = lastErr.Error()
	}
	return snapshot{OK: false, Backend: "TVicPort", Error: errText}
}

func writeFan(t *tvicPort, kinds []ecPorts, value byte) (snapshot, error) {
	var lastErr error
	for _, p := range kinds {
		if _, err := t.readEC(p, fanRegister); err != nil {
			lastErr = err
			continue
		}
		if err := t.writeEC(p, fanRegister, value); err != nil {
			return snapshot{OK: false, Backend: "TVicPort", PortType: p.Name, Error: err.Error()}, err
		}
		time.Sleep(100 * time.Millisecond)
		s := readSnapshot(t, []ecPorts{p})
		return s, nil
	}
	if lastErr == nil {
		lastErr = errors.New("no EC port type worked")
	}
	return snapshot{OK: false, Backend: "TVicPort", Error: lastErr.Error()}, lastErr
}

func emit(v any) int {
	encoder := json.NewEncoder(os.Stdout)
	encoder.SetIndent("", "  ")
	if err := encoder.Encode(v); err != nil {
		fmt.Fprintln(os.Stderr, err)
		return 1
	}
	return 0
}

func main() {
	portKind := flag.String("type", "auto", "EC port type: auto, type1, type2")
	flag.Parse()
	args := flag.Args()
	if len(args) == 0 {
		fmt.Fprintln(os.Stderr, "usage: tvic-ec-helper [--type auto|type1|type2] probe|snapshot|bios|level N|max|read HEX|write HEX HEX")
		os.Exit(2)
	}

	t, err := openTVic()
	if err != nil {
		os.Exit(emit(snapshot{OK: false, Backend: "TVicPort", Error: err.Error()}))
	}
	defer t.close()

	kinds := selectPorts(*portKind)
	switch strings.ToLower(args[0]) {
	case "probe", "snapshot":
		s := readSnapshot(t, kinds)
		if s.OK {
			os.Exit(emit(s))
		}
		emit(s)
		os.Exit(1)
	case "bios":
		s, err := writeFan(t, kinds, biosAuto)
		emit(s)
		if err != nil {
			os.Exit(1)
		}
	case "level":
		if len(args) < 2 {
			fmt.Fprintln(os.Stderr, "missing level")
			os.Exit(2)
		}
		rawValue := 0
		if strings.EqualFold(args[1], "max") {
			rawValue = fanMax
		} else {
			level, err := strconv.Atoi(args[1])
			if err != nil || !((level >= 1 && level <= 7) || level == fanMax) {
				fmt.Fprintln(os.Stderr, "level must be 1..7, 64, or max")
				os.Exit(2)
			}
			rawValue = level
		}
		if rawValue < 1 || rawValue > 255 {
			fmt.Fprintln(os.Stderr, "level value is out of byte range")
			os.Exit(2)
		}
		s, err := writeFan(t, kinds, byte(rawValue))
		emit(s)
		if err != nil {
			os.Exit(1)
		}
	case "read":
		if len(args) < 2 {
			fmt.Fprintln(os.Stderr, "missing register")
			os.Exit(2)
		}
		offset64, err := strconv.ParseUint(strings.TrimPrefix(args[1], "0x"), 16, 8)
		if err != nil {
			fmt.Fprintln(os.Stderr, err)
			os.Exit(2)
		}
		var lastErr error
		for _, p := range kinds {
			value, err := t.readEC(p, byte(offset64))
			if err == nil {
				os.Exit(emit(map[string]any{"ok": true, "backend": "TVicPort", "port_type": p.Name, "register": int(offset64), "value": int(value)}))
			}
			lastErr = err
		}
		os.Exit(emit(map[string]any{"ok": false, "backend": "TVicPort", "error": lastErr.Error()}))
	case "write":
		if len(args) < 3 {
			fmt.Fprintln(os.Stderr, "missing register/value")
			os.Exit(2)
		}
		offset64, err1 := strconv.ParseUint(strings.TrimPrefix(args[1], "0x"), 16, 8)
		value64, err2 := strconv.ParseUint(strings.TrimPrefix(args[2], "0x"), 16, 8)
		if err1 != nil || err2 != nil {
			fmt.Fprintln(os.Stderr, "register/value must be hex bytes")
			os.Exit(2)
		}
		var lastErr error
		for _, p := range kinds {
			if err := t.writeEC(p, byte(offset64), byte(value64)); err == nil {
				os.Exit(emit(map[string]any{"ok": true, "backend": "TVicPort", "port_type": p.Name, "register": int(offset64), "value": int(value64)}))
			} else {
				lastErr = err
			}
		}
		os.Exit(emit(map[string]any{"ok": false, "backend": "TVicPort", "error": lastErr.Error()}))
	default:
		fmt.Fprintln(os.Stderr, "unknown command:", args[0])
		os.Exit(2)
	}
}
