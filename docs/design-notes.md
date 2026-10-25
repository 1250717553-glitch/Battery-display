# Design notes

Condensed from the original Chinese requirements and delivery notes. Sensitive
machine-specific details (device VID/PID, screen resolution) are intentionally
omitted.

## Goals

A tray-resident tool that reports battery and connection state for:

1. A 2.4 GHz wireless receiver and its paired peripherals.
2. All paired Bluetooth devices (mouse / keyboard / headset).

Non-goals: never embed into the taskbar; no always-on floating overlay.

## Technology baseline

- C# / .NET 8, WPF (chosen over WinUI 3 for mature tray, popup and DPI support
  and lower memory footprint).
- Bluetooth via the Windows Runtime API (`Windows.Devices.Bluetooth`).
- 2.4 GHz via raw HID (SetupAPI + `hid.dll`), read-only.
- Runtime detection of taskbar position, auto-hide, DPI and theme — never
  hard-coded.

## Device abstraction

`IDeviceProvider` exposes `ProbeAsync`, `ReadBatteryAsync`, `ReadChargingAsync`
and `CapabilitiesFor`. Three support levels:

| Level | Meaning |
|---|---|
| `Available` | A real battery reading exists |
| `ConnectionOnly` | Device reachable, battery unreadable |
| `Unknown` | Device cannot even be identified |

Two built-in providers: HID (2.4 GHz) and Bluetooth (GATT / classic). New
receiver families are supported by adding a provider, not by branching the UI.

## Refresh policy

- Default polling interval 45 s; offline devices back off.
- Bluetooth battery is read on demand (opening the window, clicking the widget)
  rather than on a timer, to avoid repeatedly establishing GATT connections
  and disturbing audio devices.
- Duplicate refresh requests are coalesced so a burst of UI events does not
  trigger repeated enumeration.

## Alerting

Hysteresis is mandatory: an alert fires once when a device drops below the
threshold, and only re-arms after the charge recovers above the reset value or
the device reconnects. Charging devices are skipped. Unreadable batteries never
alert and never render as 0%.

## Known limitations

- Many 2.4 GHz receivers never report battery over HID without a vendor
  command. This project intentionally does not send vendor commands, so those
  devices show "connected, battery unknown".
- Classic Bluetooth headsets usually do not expose a battery percentage.
- Multi-monitor and mixed-DPI taskbar geometry is implemented but has only been
  verified on a single-display machine.
