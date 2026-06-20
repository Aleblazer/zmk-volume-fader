# zmk-volume-fader

[![Build](https://github.com/Aleblazer/zmk-volume-fader/actions/workflows/build.yml/badge.svg)](https://github.com/Aleblazer/zmk-volume-fader/actions/workflows/build.yml)

A small Windows app that maps two physical slide faders on a ZMK keyboard to the
volume of two Windows audio output devices — so you can ride, say, **Game** and
**Chat** levels independently without alt-tabbing to a mixer.

The keyboard's dongle reports the faders over USB HID; this app reads that and
drives each chosen output's volume in real time. It tucks into the system tray
and keeps working in the background.

<!-- TODO: add a screenshot of the window here, e.g. docs/screenshot.png -->

## How it works

Your ZMK build wires a slide potentiometer to an ADC channel and exposes it
through [zmk-hid-io](https://github.com/Aleblazer/zmk-hid-io/tree/absolute-faders)
as a HID joystick (Generic Desktop, **report id 2**): `X` = first fader, `Y` =
second fader, each a **signed 16-bit little-endian** axis carrying the raw wiper
voltage (~0..3300). In the raw report, bytes 1–2 are the first fader and bytes
3–4 the second.

That 16-bit path uses a fork of zmk-hid-io patched to forward absolute axes
(upstream only handled relative motion) and widened from 8-bit to 16-bit so a
pot's taper-compressed top of travel keeps its resolution.

## Requirements

- **Windows** (uses the Core Audio API and WinForms).
- **.NET 8 SDK** to build/run:
  ```bat
  winget install Microsoft.DotNet.SDK.8
  ```
- A **ZMK keyboard** whose dongle exposes the faders as described above — ZMK's
  default USB VID `0x1D50` / PID `0x615E`.

## Build & run

```bat
cd ZmkVolumeFader
dotnet run
```

To produce a standalone `.exe` (no SDK needed to run it):

```bat
dotnet publish ZmkVolumeFader -c Release -r win-x64 --self-contained false
```

## Usage

The app auto-finds the dongle (VID `0x1D50` / PID `0x615E`, by the Joystick HID
usage) and shows a row per fader:

- **Output dropdown** — pick the Windows render device each fader controls (e.g.
  a headset's "Game" and "Chat" endpoints). Move a fader and its device's volume
  follows.
- **Max %** cap — the throw scales into `0..cap`, so a cap of 60 means bottom =
  0%, top = 60%, middle = 30% — handy for outputs that get painfully loud past a
  point. Changing the cap applies immediately, so dropping it instantly pulls a
  loud device back down.
- A live bar and a `raw (min-max)` readout per fader (the raw readout is for
  calibration).

Minimizing sends the window straight to the system tray; closing it asks whether
to minimize to the tray or exit. While in the tray it keeps driving volume — use
the tray icon's **Open** / **Exit** menu or double-click to bring it back.

Device choices and caps are saved to `%APPDATA%\ZmkVolumeFader\settings.json`.

## Calibration

The pot reads strongly compressed at the top of travel, so the value→percent
mapping is a piecewise curve (`Curve` in `ZmkVolumeFader/MainForm.cs`), not
linear — it inverts the taper so the throw feels ~linear. The end points are
continuous dead bands (value 0 → 0%, value ≥ the last point → 100%), so the
bottom rests cleanly and the top reaches full volume without a cliff. Output
hysteresis keeps a parked level from flip-flopping between two percentages.

To recalibrate after a pot/wiring change: sweep both faders fully, read the
`(min-max)` off each label, set `Curve`'s first point to `(min, 0)` and last to
`(max, 100)`, and adjust the mids to taste.

## fader_read.py — optional Python byte-dumper

A minimal raw-report reader, handy for verifying the dongle without .NET:

```bat
py -m pip install hidapi
py fader_read.py
```

## License

[MIT](LICENSE)
