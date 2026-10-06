# Relative Pen Mode

An [OpenTabletDriver](https://github.com/OpenTabletDriver/OpenTabletDriver) output mode for **KDE Plasma
on Wayland**: relative, mouse-style motion that stays a **pen**, so pressure, tilt and pen buttons still
reach every app (Krita, browsers, and so on).

Stock Relative Mode drives a virtual *mouse*, so apps never see pressure. Artist Mode keeps pressure,
but it's absolute. This plugin does both at once.

## Requirements
- Linux with **KDE Plasma 6 on Wayland**. It relies on KWin's per-device relative tablet mode, so GNOME,
  X11, Windows and macOS won't work.
- OpenTabletDriver **0.6.7**.
- Any tablet OTD supports. Tested on a One by Wacom CTL-672. Tilt is untested, so reports are welcome.

## Install
1. Build it (below), or grab `RelativePenMode.dll` from a release.
2. Copy it to `~/.config/OpenTabletDriver/Plugins/Relative Pen Mode/`.
3. Restart OTD (`systemctl --user restart opentabletdriver`).
4. In the OTD window: **Output** tab → pick **Relative Pen Mode** → **Save**.

That's all. The plugin sets up KDE by itself. The usual Output-tab fields (Sensitivity, Rotation, Reset
Time) work the same as in stock Relative Mode.

## Optional extras (Filters tab)
Tick an entry and click **Save**.

**Relative Pen: Mouse Mode on Shortcut (default Space)**: while you hold the key, the pen drives a
plain mouse (tip = left click, buttons = right/middle); let go and it's a pen again. For apps that want a
mouse, e.g. browser pointer lock. It switches only with the tip and buttons up, and stays a mouse while you
lift the pen to reposition. It's a KDE global shortcut, so you can rebind it in System Settings → Shortcuts
("OpenTabletDriver Relative Pen"). A global shortcut eats its key, so it's only live while the pen is near
the tablet (plus 2 s after): Space types normally otherwise, but not while the pen rests on the tablet.
Turning it on also turns on KDE's "sync tablet with mouse" (see the tip below), so the pen picks up where
the mouse left off.

**Relative Pen: Absolute Pen + Mouse Mode on Shortcut (default Space)**: the pen becomes absolute (the
tablet maps to the screen, like Artist Mode, set up in System Settings → Drawing Tablet under
"OpenTabletDriver Absolute Pen"), and the shortcut gives you the same relative mouse.

**Relative Pen: Mouse Mode Shortcut Toggles Instead of Hold**: press once for mouse, again for pen, with
Plasma's on-screen display saying which. Putting the pen away (2 s out of range) returns it to pen.

**Relative Pen: Freeze Cursor Position on Supported App Sliders**: lets
native apps freeze the pen cursor, e.g. during a slider drag, and receive the motion themselves. It only
does something in apps that support it (see below). Browsers can't use it: they block pages from
reaching local programs, and their own pointer lock only catches a mouse.

Tip: KDE's "sync tablet with mouse" setting (`kwriteconfig6 --file kcminputrc --group Tablet --key
SyncWithMouse true --notify`) makes the mouse and the pen pick up from each other's cursor position.

## App cursor lock (for app developers)
A Unix `SOCK_SEQPACKET` socket at `$XDG_RUNTIME_DIR/otd-relative-pen.sock`, one ASCII message per packet.

| Direction | Messages |
|---|---|
| plugin → app | `hello 1` on connect |
| app → plugin | `lock`, `unlock` |
| plugin → app | `locked` · `denied no-pen\|off\|busy` · `motion <dx> <dy>` (logical px, one per tablet report) · `unlocked request\|out-of-range\|timeout\|off` |

While locked, the cursor stays put and the motion comes to you instead. A lock always ends when your
connection closes, the pen leaves the tablet, the filter is unticked, or 60 s pass. If the socket is
missing, the feature isn't available.

## Build
```bash
git clone --recursive https://github.com/t4paN/otd-relative-pen
cd otd-relative-pen/RelativePenMode
dotnet build -c Release        # .NET 8 SDK → bin/Release/net8.0/RelativePenMode.dll
```
`ref-otd` is OTD's source pinned to v0.6.7 (a submodule); the plugin compiles against it.

`relpen.sh on|off|status` is the author's helper for switching between this mode and stock Relative Mode.
`off` restores a settings backup you'd need to have made yourself.

## How it works
It feeds OTD's relative motion into its own virtual pen, **"OpenTabletDriver Relative Pen"**, and turns
on KWin's per-device relative mode for it (via `busctl --user`). KWin then moves the cursor by the pen's
deltas.

Why its own pen, and not OTD's Artist-Mode one: KWin's relative mode moves the cursor by libinput's
tablet delta, which the flat pen filter computes as `mm * 96/25.4*2.5` (≈9.45 px per mm). OTD's
Artist-Mode pen declares 1 screen px = 0.01 mm, so reused as-is the cursor crawls at ~1/10 speed, and its
screen-sized range gives only ~250 cursor px of travel. This pen declares 254 units/px at 2400 units/mm,
exactly 1 px = 1 cursor px, with a 4M px range, so the edge is never reached.

## License
LGPL-3.0-or-later, like OpenTabletDriver.
