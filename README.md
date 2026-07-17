# zmk-volume-fader

[![Build](https://github.com/Aleblazer/zmk-volume-fader/actions/workflows/build.yml/badge.svg)](https://github.com/Aleblazer/zmk-volume-fader/actions/workflows/build.yml)

**ZMK Volume Fader** rides the volume of your apps and audio devices in real time —
with **physical slide faders on a ZMK keyboard**, **on-screen faders you drag with
the mouse**, **global hotkeys**, or any mix of the three. A ZMK keyboard is
**optional**: everything works with no hardware at all.

Point each fader at a **Windows output device**, a **single app**, or a **group of
apps**, and ride levels — say game vs. chat, or Discord vs. everything else —
without alt-tabbing to a mixer. It tucks into the system tray and keeps working in
the background.

<!-- TODO: add a screenshot of the window here, e.g. docs/screenshot.png -->

## Features

- **Any number of configured faders** (up to 8 physical axes per device), each independently controlling one of:
  - an **output device** (with ranked fallback across devices),
  - one **app's** volume (followed across every device it's playing on),
  - a **category** of apps that move together, or
  - **"Everything Else"** — every running app not assigned to a category (and not
    already targeted directly by another fader, so they never fight).
- **Physical faders** — slide pots on a ZMK keyboard, read over USB HID *(optional hardware)*.
- **Virtual faders** — on-screen faders you drag with the mouse. **No hardware required.**
- **Global hotkeys** for virtual faders — bind Volume Up / Down / Mute to any key
  (**F13–F24** are ideal) or a modifier combo. Keys pass through, so they still work
  in the focused app; stepping is smoothed and the step size is configurable.
- **Unified Fader Layout** — combine physical axes from multiple devices with virtual
  faders in any order; sweep to detect hardware, then reorder, rename, or remove in place.
- **Categories editor**, **ranked default outputs** with automatic hotplug failover,
  and per-output **Max %** caps.
- **Light / dark / auto theme**, **start with Windows**, **minimize to tray**.
- One global custom layout with stable physical device/axis bindings; settings persist in
  `%APPDATA%\ZmkVolumeFader\settings.v2.json`.

## Install

Download the latest release from
[Releases](https://github.com/Aleblazer/zmk-volume-fader/releases) (Windows 10/11 x64):

- **`ZmkVolumeFader-<version>-win-x64.msi`** — recommended installer; adds the app to
  the Start Menu and Windows Installed Apps, supports in-place upgrades, and uninstalls
  cleanly.
- **`ZmkVolumeFader-<version>-win-x64.exe`** — portable, self-contained single file.

Neither build needs a separate .NET installation. Existing settings in
`%APPDATA%\ZmkVolumeFader` are preserved when installing, upgrading, or uninstalling.

> The build is **unsigned**, so Windows SmartScreen shows a "Windows protected your
> PC" prompt on first run. Click **More info → Run anyway**.

## Requirements

- **Windows 10/11** (uses the Core Audio API and WinForms).
- *(optional)* a **ZMK keyboard** whose dongle exposes slide faders over USB HID —
  needed only for **physical** faders. Without one, use virtual faders + hotkeys.
- *(to build from source)* the **.NET 8 SDK**:
  ```bat
  winget install Microsoft.DotNet.SDK.8
  ```

## Build & run

```bat
cd ZmkVolumeFader
dotnet run
```

Standalone single-file `.exe` (matches the released build):

```bat
dotnet publish ZmkVolumeFader -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
```

Build the tested portable executable and MSI release assets together:

```powershell
.\tools\build-release.ps1
```

## Usage

Open **Fader Layout** on the main window—or **Options → Fader layout…**—to add faders:

- **Add physical fader** — choose a detected device and sweep the fader fully
  bottom-to-top; the app detects its axis and captures its range.
- **Add virtual fader** — adds an on-screen fader you drag with the mouse.
- Reorder with ▲/▼, remove with ✕, rename in place, or use **Source…** to move an
  existing physical fader to another device/axis without losing its target settings.
- Connected, offline, and unavailable physical sources are identified in the layout;
  the editor prevents the same device axis from being assigned twice.

With no faders configured, the main window shows an **"Add fader"** card that jumps
straight into setup.

Each fader card has:

- **Output / Apps / Categories** tabs — choose what the fader controls, then pick the
  target from the dropdown below. The app list mirrors the live Windows mixer;
  Categories includes the built-in **Everything Else** catch-all.
- **Max %** cap (physical faders) — the throw scales into `0..cap`, so a cap of 60
  means bottom = 0%, top = 60%, middle = 30%. Changing it applies immediately.
- **Hotkeys** and **Remove** (virtual faders) — assign global hotkeys, or delete the
  fader (with confirmation).

### Virtual faders & hotkeys

Drag a virtual fader with the mouse, or click **Hotkeys** to bind **Volume Up / Down /
Mute** to global keys. **F13–F24** are ideal — nothing else uses them, so they need no
modifier — and modifier combos (e.g. `Ctrl+Alt+↑`) work too. Keys are observed, not
swallowed, so they keep doing their normal job in whatever app is focused. The step per
press is configurable and the change is smoothed so holding a key ramps cleanly. On a
ZMK keyboard, map a key (or a dedicated cluster) to F13–F24 and it becomes an app volume
control — no analog fader hardware needed.

### Output failover & categories

**Set Default Outputs** ranks a fader's preferred output devices; the top present one is
driven and it fails over automatically on plug/unplug. **Manage Categories** groups apps
so a fader can move them together.

### Tray & persistence

Minimizing sends the window straight to the tray; closing asks whether to minimize or
exit (configurable in Options: ask / always tray / always exit). While in the tray it
keeps driving volume — use the tray icon's **Open** / **Exit**
menu, or double-click to bring it back. Everything — faders, targets, order, hotkeys,
calibration, theme — is saved to `%APPDATA%\ZmkVolumeFader\settings.v2.json`.

## Calibration (physical faders)

In **Options**, each physical fader has a **taper preset** (Linear / Audio / Straight)
plus a **Record** button: hit Record, sweep the fader end-to-end, stop, then pick the
taper that matches your pot — the preview updates as you move it. This corrects a slide
pot's compressed top-of-travel so the throw feels even, and output hysteresis keeps a
parked level from flip-flopping between two percentages. Choose a **reversed** taper
when GND and 3V3 are wired to the opposite ends of the potentiometer; this mirrors the
electrical compensation curve while preserving low-to-high travel. **Invert** remains
a separate final direction toggle,
so either setting can be used independently or together. Each physical fader can also set a
**Mute threshold** — a mixer-style endpoint detent that forces 0% near the chosen raw
threshold and follows the effective 0% endpoint. Virtual faders need no
calibration.

## Physical faders — ZMK firmware (optional)

For physical faders, your ZMK build wires a slide potentiometer to an ADC channel and
forwards it through a fork of
[zmk-hid-io](https://github.com/Aleblazer/zmk-hid-io/tree/absolute-faders). The faders
are emitted entirely under a **vendor HID page (`0xFF00`), report id 2**: up to **eight
signed 16-bit little-endian axes**, each carrying the raw wiper voltage (~0..3300). The
app finds the dongle by ZMK's default USB VID `0x1D50` / PID `0x615E` and the vendor
usage `0xFF000001`, and reads whatever axes are present.

The report sits on a vendor page (rather than Generic Desktop / Joystick) on purpose, so
neither Windows nor games treat the keyboard as a game controller. The fork also forwards
**absolute** axes (upstream only handled relative motion) and widens them from 8-bit to
16-bit so a pot's taper-compressed top of travel keeps its resolution.

## fader_read.py — optional Python byte-dumper

A minimal raw-report reader, handy for verifying the dongle without .NET:

```bat
py -m pip install hidapi
py fader_read.py
```

## Leak diagnostics

The executable accepts opt-in isolation switches for troubleshooting:

- `--diag-no-volume` keeps device/session tracking active but sends no volume setters.
- `--diag-sink` reads and discards HID reports before the fader pipeline.
- `--diag-synth` drives physical faders with a synthetic triangle wave.
- `--diag-no-draw` and `--diag-no-tray` isolate UI repaint/notification work.
- `--diag-audio-stats` shows cumulative endpoint and session setter counts in the
  window title, making those calls easy to correlate with pool growth.

Normal operation uses event-driven endpoint, session, and HID discovery. Desired
volumes are coalesced, unchanged targets are skipped, and actual Core Audio setters
share one global call budget after category/session fan-out.

## License

[MIT](LICENSE)
