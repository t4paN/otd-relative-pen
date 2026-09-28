using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenTabletDriver.Native.Linux;
using OpenTabletDriver.Native.Linux.Evdev;
using OpenTabletDriver.Native.Linux.Evdev.Structs;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Platform.Pointer;
using OpenTabletDriver.Plugin.Tablet;

namespace RelativePenMode
{
    // Relative ("mouse") mode that stays a PEN.
    //
    // Stock Relative Mode drives a virtual MOUSE, so no app ever sees pressure.
    // Here OTD's relative maths (sensitivity, rotation, reset delay) produces the
    // deltas as usual, and we feed them into our own virtual pen as a hidden
    // accumulated position. KWin's per-device relative mode (tabletToolIsRelative)
    // turns changes of that position into cursor motion; the compositor owns the
    // cursor, so mixing mouse and pen never jumps it.
    //
    // Why our own pen and not OTD's Artist-Mode one: KWin's relative path moves the
    // cursor by libinput's tablet delta, which libinput derives from device units /
    // resolution (mm) * 96/25.4*2.5 (filter-tablet.c, flat pen filter). OTD's virtual
    // tablet declares 1 screen px = 0.01 mm, so the cursor crawled at ~1/10 speed and
    // the whole screen-sized range was only ~250 cursor px of travel before it hit
    // the clamp (the "cropped" feel). Our pen declares units so 1 px here = 1 cursor
    // px, with a range so large the edge is never reached in practice.
    [PluginName("Relative Pen Mode"), SupportedPlatform(PluginPlatform.Linux)]
    public class RelativePenMode : RelativeOutputMode
    {
        private static readonly Lazy<RelativePen> SharedPen = new(() => new RelativePen());

        // One uinput device for the daemon's lifetime: OTD rebuilds the output mode on
        // every settings Save, and a fresh device each time would churn KWin's list.
        public override IRelativePointer Pointer
        {
            set => throw new NotSupportedException();
            get => SharedPen.Value;
        }

        // OTD hands a new element list on every Save; whether our Filters-tab entries are
        // in it are the on/off switches for the app cursor lock and hover-as-mouse.
        private object seenElements;

        protected override void OnOutput(IDeviceReport report)
        {
            if (!ReferenceEquals(Elements, seenElements))
            {
                seenElements = Elements;
                CursorLock.SetAllowed(Elements?.Any(e => e is AppCursorLock) == true);
                SharedPen.Value.SetHoverMouse(Elements?.Any(e => e is HoverMouse) == true);
            }
            base.OnOutput(report);
        }
    }

    // The Filters-tab switch for hover-as-mouse (see RelativePen). Position None: never in the
    // report pipeline, RelativePenMode just looks for it, like AppCursorLock.
    [PluginName("Relative Pen: Act as Mouse While Hovering")]
    public class HoverMouse : IPositionedPipelineElement<IDeviceReport>
    {
        public PipelinePosition Position => PipelinePosition.None;
        public event Action<IDeviceReport> Emit;
        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }

    // Deliberately NOT an IMouseButtonHandler, so Adaptive Bindings route the tip and
    // barrel buttons to pen actions instead of mouse clicks.
    //
    // Hover-as-mouse (Filters-tab switch): while the tip is up, motion goes out through a
    // virtual MOUSE, so any app's pointer lock (browsers, games, Chisel's hover sliders)
    // catches it. Wayland only ever locks a mouse. On tip-down the pen enters proximity where
    // the cursor is (KWin's [Tablet] SyncWithMouse hands it the mouse position, and libinput
    // zeroes the entering frame's delta), draws with pressure, and leaves again on tip-up.
    // Pressure only exists while touching anyway, so nothing is lost.
    internal sealed class RelativePen : IRelativePointer, IPenActionHandler, IPressureHandler,
        ITiltHandler, IEraserHandler, ISynchronousPointer
    {
        public const string DeviceName = "OpenTabletDriver Relative Pen"; // "OpenTabletDriver*" = libinput quirk: smoothing off
        public const string MouseName = "OpenTabletDriver Relative Pen (Hover Mouse)";

        // 1 px of accumulator = UnitsPerPx device units; libinput turns units into mm via
        // Resolution, then mm into logical px via * 96/25.4*2.5. 254/2400 mm * 9.449 = 1.000,
        // so OTD's sensitivity means the same here as in stock Relative Mode.
        // The mouse matches it: 1 REL unit = 1 px, given the flat accel profile at speed 0
        // (libinput filter-flat.c: factor = 1 + speed; no DPI hint = 1000, i.e. unscaled),
        // which KdeRelative.MouseFlat sets on it.
        private const int UnitsPerPx = 254;
        private const int Resolution = 2400;           // units per mm
        private const double RangePx = 4_000_000;       // ~1e9 units, well inside int32
        private const int MaxPressure = ushort.MaxValue;

        private readonly EvdevDevice device;
        private EvdevDevice mouse;                      // created the first time hover-as-mouse is on
        private double x = RangePx / 2, y = RangePx / 2;
        private Vector2 mouseRemainder;                 // REL is whole px; carry the fraction
        private bool eraser, hoverMouse, touching, penIn, penDirty, mouseDirty;
        private int pressure, tiltX, tiltY;
        // Which device and code a held button went out on, so its release follows it even
        // if the pen switched between mouse and pen in between.
        private readonly Dictionary<PenAction, (EvdevDevice Device, EventCode Code)> held = new();

        public unsafe RelativePen()
        {
            device = new EvdevDevice(DeviceName);
            device.EnableProperty(InputProperty.INPUT_PROP_POINTER);
            device.EnableType(EventType.EV_ABS);

            var xAbs = new input_absinfo { maximum = (int)(RangePx * UnitsPerPx), resolution = Resolution };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_X, (IntPtr)(&xAbs));
            var yAbs = new input_absinfo { maximum = (int)(RangePx * UnitsPerPx), resolution = Resolution };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_Y, (IntPtr)(&yAbs));
            var pressureAbs = new input_absinfo { maximum = MaxPressure };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_PRESSURE, (IntPtr)(&pressureAbs));
            var xTilt = new input_absinfo { minimum = -64, maximum = 63, resolution = 57 };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_TILT_X, (IntPtr)(&xTilt));
            var yTilt = new input_absinfo { minimum = -64, maximum = 63, resolution = 57 };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_TILT_Y, (IntPtr)(&yTilt));

            device.EnableTypeCodes(EventType.EV_KEY, Keys);

            var result = device.Initialize();
            if (result == ERRNO.NONE)
            {
                Log.Write("Relative Pen Mode", $"Virtual pen '{DeviceName}' created");
                Task.Run(KdeRelative.Enable);
                CursorLock.Start();
            }
            else
                Log.WriteNotify("Relative Pen Mode", $"Failed to create virtual pen (error {result}); is uinput loaded?", LogLevel.Error);
        }

        private static readonly EventCode[] Keys =
        [
            EventCode.BTN_TOUCH, EventCode.BTN_STYLUS, EventCode.BTN_STYLUS2, EventCode.BTN_STYLUS3,
            EventCode.BTN_TOOL_PEN, EventCode.BTN_TOOL_RUBBER,
        ];

        private EventCode Tool => eraser ? EventCode.BTN_TOOL_RUBBER : EventCode.BTN_TOOL_PEN;
        private bool AsPen => !hoverMouse || touching;

        // From RelativePenMode on OTD's thread, like every other call here.
        public void SetHoverMouse(bool on)
        {
            if (on && mouse == null)
                mouse = CreateMouse();
            on &= mouse != null;
            if (on == hoverMouse) return;
            hoverMouse = on;
            Log.Write("Relative Pen Mode", on ? "Hover-as-mouse on" : "Hover-as-mouse off");
            if (on && !touching && penIn)
                PenOut();
        }

        public void SetPosition(Vector2 delta)
        {
            delta = CursorLock.Divert(delta);   // zero while an app holds the cursor lock
            // Clamp is a backstop only: 4M px from centre is ~2 km of hand travel.
            x = Math.Clamp(x + delta.X, 0, RangePx);
            y = Math.Clamp(y + delta.Y, 0, RangePx);
            if (AsPen)
            {
                device.Write(EventType.EV_KEY, Tool, 1);
                device.Write(EventType.EV_ABS, EventCode.ABS_X, (int)Math.Round(x * UnitsPerPx));
                device.Write(EventType.EV_ABS, EventCode.ABS_Y, (int)Math.Round(y * UnitsPerPx));
                penIn = penDirty = true;
            }
            else
            {
                var d = delta + mouseRemainder;
                var whole = new Vector2(MathF.Truncate(d.X), MathF.Truncate(d.Y));
                mouseRemainder = d - whole;
                if (whole.X != 0) mouse.Write(EventType.EV_REL, EventCode.REL_X, (int)whole.X);
                if (whole.Y != 0) mouse.Write(EventType.EV_REL, EventCode.REL_Y, (int)whole.Y);
                mouseDirty |= whole != Vector2.Zero;
            }
        }

        // Pressure and tilt are remembered while hovering as a mouse and go out with the pen.
        public void SetPressure(float percentage)
        {
            pressure = (int)(MaxPressure * Math.Clamp(percentage, 0f, 1f));
            if (!AsPen) return;
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, pressure);
            penDirty = true;
        }

        public void SetTilt(Vector2 tilt)
        {
            (tiltX, tiltY) = ((int)tilt.X, (int)tilt.Y);
            if (!AsPen) return;
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_X, tiltX);
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_Y, tiltY);
            penDirty = true;
        }

        public void SetEraser(bool isEraser)
        {
            if (eraser == isEraser)
                return;
            if (penIn)
            {
                device.Write(EventType.EV_KEY, Tool, 0);   // old tool leaves before the new one enters
                device.Sync();
            }
            eraser = isEraser;
        }

        // Touch comes from the Tip/Eraser bindings (not pressure > 0) so OTD's tip
        // activation threshold still applies.
        public void Activate(PenAction action) => SetAction(action, 1);
        public void Deactivate(PenAction action) => SetAction(action, 0);

        private void SetAction(PenAction action, int state)
        {
            if (action is PenAction.Tip or PenAction.Eraser)
            {
                if (state == 1)
                {
                    touching = true;
                    if (!penIn) PenIn();
                    device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, pressure);
                }
                device.Write(EventType.EV_KEY, EventCode.BTN_TOUCH, state);
                device.Sync();
                if (state == 0)
                {
                    touching = false;
                    if (hoverMouse) PenOut();
                }
                return;
            }

            if (state == 0)
            {
                if (held.Remove(action, out var h))
                {
                    h.Device.Write(EventType.EV_KEY, h.Code, 0);
                    h.Device.Sync();
                }
                return;
            }
            // While hovering as a mouse the barrel buttons are mouse buttons, mapped the way
            // libinput maps pen buttons: STYLUS2 = right, STYLUS = middle.
            EventCode? code = (action, AsPen) switch
            {
                (PenAction.BarrelButton1, true) => EventCode.BTN_STYLUS2,   // matches Artist Mode: button 1 = right click
                (PenAction.BarrelButton2, true) => EventCode.BTN_STYLUS,
                (PenAction.BarrelButton3, true) => EventCode.BTN_STYLUS3,
                (PenAction.BarrelButton1, false) => EventCode.BTN_RIGHT,
                (PenAction.BarrelButton2, false) => EventCode.BTN_MIDDLE,
                (PenAction.BarrelButton3, false) => EventCode.BTN_SIDE,
                _ => null,
            };
            if (code is { } c)
            {
                var dev = AsPen ? device : mouse;
                held[action] = (dev, c);
                dev.Write(EventType.EV_KEY, c, 1);
                dev.Sync();
            }
        }

        // Enter proximity at the current (hidden) position, in a frame of its own with zero
        // pressure, so the touch that follows lands where KWin put the cursor.
        private void PenIn()
        {
            device.Write(EventType.EV_KEY, Tool, 1);
            device.Write(EventType.EV_ABS, EventCode.ABS_X, (int)Math.Round(x * UnitsPerPx));
            device.Write(EventType.EV_ABS, EventCode.ABS_Y, (int)Math.Round(y * UnitsPerPx));
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_X, tiltX);
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_Y, tiltY);
            device.Sync();
            penIn = true;
        }

        private void PenOut()
        {
            foreach (var key in Keys)
                device.Write(EventType.EV_KEY, key, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            device.Sync();
            penIn = penDirty = false;
        }

        // Pen left the tablet: go out of proximity and recentre. Safe to jump here —
        // libinput reports a zero delta for the entering-proximity frame, and KWin
        // places a relative tool from its own state (or the mouse's, with SyncWithMouse).
        public void Reset()
        {
            foreach (var key in Keys)
                device.Write(EventType.EV_KEY, key, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            penDirty = true;
            penIn = touching = eraser = false;
            foreach (var (dev, code) in held.Values)
                if (dev == mouse)
                {
                    mouse.Write(EventType.EV_KEY, code, 0);
                    mouseDirty = true;
                }
            held.Clear();
            x = y = RangePx / 2;
            mouseRemainder = Vector2.Zero;
            CursorLock.PenLeft();
        }

        public void Flush()
        {
            if (penDirty) device.Sync();
            if (mouseDirty) mouse.Sync();
            penDirty = mouseDirty = false;
        }

        private static EvdevDevice CreateMouse()
        {
            var m = new EvdevDevice(MouseName);
            m.EnableTypeCodes(EventType.EV_REL, EventCode.REL_X, EventCode.REL_Y);
            m.EnableTypeCodes(EventType.EV_KEY, EventCode.BTN_LEFT, EventCode.BTN_RIGHT, EventCode.BTN_MIDDLE, EventCode.BTN_SIDE);
            var result = m.Initialize();
            if (result != ERRNO.NONE)
            {
                Log.WriteNotify("Relative Pen Mode", $"Failed to create hover mouse (error {result})", LogLevel.Error);
                return null;
            }
            Log.Write("Relative Pen Mode", $"Virtual mouse '{MouseName}' created");
            Task.Run(KdeRelative.MouseFlat);
            Task.Run(KdeRelative.EnsureSyncWithMouse);
            return m;
        }
    }

    // Turns on KWin's relative mode for our pen, so installing the plugin is the whole
    // setup. KWin persists it by device name, and it's re-applied each daemon start in
    // case the user's config was reset. Uses busctl (ships with systemd) rather than a
    // D-Bus library, so the plugin stays a single DLL. Non-KDE desktops: logs and gives up.
    internal static class KdeRelative
    {
        private const string Svc = "org.kde.KWin", Root = "/org/kde/KWin/InputDevice", Iface = "org.kde.KWin.InputDevice";

        public static void Enable()
        {
            if (Find(RelativePen.DeviceName) is not { } dev)
                return;
            var ok = Set(dev, "tabletToolIsRelative", "b", "true");
            Log.Write("Relative Pen Mode", ok
                ? $"KDE relative mode enabled on {dev}"
                : "Could not enable KDE relative mode", ok ? LogLevel.Info : LogLevel.Warning);
        }

        // Hover mouse: flat acceleration at speed 0, so it moves 1:1 like the pen, whatever
        // the user's mouse settings are.
        public static void MouseFlat()
        {
            if (Find(RelativePen.MouseName) is not { } dev)
                return;
            var ok = Set(dev, "pointerAccelerationProfileFlat", "b", "true")
                && Set(dev, "pointerAcceleration", "d", "0");
            Log.Write("Relative Pen Mode", ok
                ? $"Hover mouse set to flat acceleration on {dev}"
                : "Could not set flat acceleration on the hover mouse", ok ? LogLevel.Info : LogLevel.Warning);
        }

        // Hover-as-mouse needs KWin to hand the pen the mouse's position on proximity-in, or
        // strokes would start where the pen last was. A global KDE setting ([Tablet]
        // SyncWithMouse); KWin watches the file, so --notify applies it live.
        public static void EnsureSyncWithMouse()
        {
            if (Run("kreadconfig6", "--file", "kcminputrc", "--group", "Tablet", "--key", "SyncWithMouse")?.Trim() == "true")
                return;
            var ok = Run("kwriteconfig6", "--file", "kcminputrc", "--group", "Tablet", "--key", "SyncWithMouse", "true", "--notify") != null;
            Log.Write("Relative Pen Mode", ok
                ? "KDE 'sync tablet with mouse' turned on (needed by hover-as-mouse)"
                : "Could not turn on KDE's [Tablet] SyncWithMouse; strokes may start off-cursor", ok ? LogLevel.Info : LogLevel.Warning);
        }

        // KWin's sysname (eventNN) for the device with exactly this name, or null.
        private static string Find(string deviceName)
        {
            // The device shows up in KWin a moment after uinput creates it.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(250);
                var list = Busctl("get-property", Svc, Root, "org.kde.KWin.InputDeviceManager", "devicesSysNames");
                if (list == null)
                {
                    if (attempt == 0) continue;
                    Log.Write("Relative Pen Mode", $"KWin not reachable; can't configure '{deviceName}' (KDE only)", LogLevel.Warning);
                    return null;
                }
                foreach (Match m in Regex.Matches(list, "\"([^\"]+)\""))
                {
                    var name = Busctl("get-property", Svc, $"{Root}/{m.Groups[1].Value}", Iface, "name");
                    if (name != null && name.Contains($"\"{deviceName}\""))
                        return m.Groups[1].Value;
                }
            }
            Log.Write("Relative Pen Mode", $"'{deviceName}' never appeared in KWin; not configured", LogLevel.Warning);
            return null;
        }

        private static bool Set(string dev, string property, string type, string value) =>
            Busctl("set-property", Svc, $"{Root}/{dev}", Iface, property, type, value) != null;

        private static string Busctl(params string[] args) => Run("busctl", ["--user", .. args]);

        private static string Run(string program, params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in args) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                return p.ExitCode == 0 ? output : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
