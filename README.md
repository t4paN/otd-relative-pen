# Relative Pen Mode — OpenTabletDriver plugin

Mouse-style (relative) pen movement that **stays a pen**, so pressure, tilt and pen
buttons reach every app (Krita, browsers, Chisel). KDE Plasma on Wayland only.

Stock OTD Relative Mode drives a virtual *mouse*, so apps never see pressure. This
output mode feeds OTD's relative motion (same Sensitivity / Rotation / Reset delay
fields in the Output tab) into its own virtual pen, **"OpenTabletDriver Relative
Pen"**, and turns on KWin's per-device relative mode for it (via `busctl --user`).
KWin then owns the cursor, so switching between mouse and pen never jumps it.

## Install
1. Drop `RelativePenMode.dll` into `~/.config/OpenTabletDriver/Plugins/Relative Pen Mode/`
   and restart the daemon (`systemctl --user restart opentabletdriver`).
2. Output tab → **Relative Pen Mode** → **Save**.

That's the whole setup. Sensitivity numbers mean the same as in stock Relative Mode.

## Build
Requires the .NET 8 SDK and OTD's source at tag **v0.6.7** in `ref-otd/`:
```
git clone --depth 1 --branch v0.6.7 https://github.com/OpenTabletDriver/OpenTabletDriver.git ref-otd
cd RelativePenMode && dotnet build -c Release
```

## Why its own pen (the units)
KWin's relative tablet mode moves the cursor by libinput's tablet delta, which the
flat pen filter computes as `mm * 96/25.4*2.5` (≈9.45 px per mm). OTD's Artist-Mode
pen declares 1 screen px = 0.01 mm, so reused as-is the cursor crawls at ~1/10 speed
and its screen-sized range is only ~250 cursor px of travel. This pen declares
254 units/px at 2400 units/mm, i.e. exactly 1 px = 1 cursor px, with a 4M px range.

## Helper
`relpen.sh on|off|status` switches between this mode and stock Relative Mode.
