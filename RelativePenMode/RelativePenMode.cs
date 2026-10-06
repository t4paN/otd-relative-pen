using System;
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

        // OTD hands a new element list on every Save; whether our Filters-tab entries are in
        // it are the on/off switches for the app cursor lock, absolute pen and mouse mode key.
        private object seenElements;

        protected override void OnOutput(IDeviceReport report)
        {
            if (!ReferenceEquals(Elements, seenElements))
            {
                seenElements = Elements;
                bool Has<T>() => Elements?.Any(e => e is T) == true;
                var pen = SharedPen.Value;
                CursorLock.SetAllowed(Has<AppCursorLock>());
                pen.SetAbsolute(Has<AbsoluteWithMouse>(), Tablet?.Properties.Specifications.Digitizer);
                pen.SetMouseModeAllowed(Has<HoldForMouse>() || Has<AbsoluteWithMouse>());
                MouseModeKey.SetToggle(Has<ToggleMouse>());
            }
            base.OnOutput(report);
        }

        // The absolute pen needs the tablet position itself; base.Transform overwrites the
        // report's position with the relative delta, so catch it first.
        protected override IAbsolutePositionReport Transform(IAbsolutePositionReport report)
        {
            SharedPen.Value.TabletPosition = report.Position;
            return base.Transform(report);
        }
    }

    // Deliberately NOT an IMouseButtonHandler, so Adaptive Bindings route the tip and
    // barrel buttons to pen actions instead of mouse clicks.
    //
    // Mouse mode (MouseModeKey held): the pen leaves proximity and the same deltas drive a
    // flat virtual mouse; tip = left, barrel 1 = right, barrel 2 = middle. It flips only with
    // the tip and buttons up, so a press and its release always go to the same device. Back
    // to pen, the next report is a proximity-in where the cursor is (KWin's [Tablet]
    // SyncWithMouse hands the pen the mouse position; libinput zeroes that frame's delta).
    //
    // Absolute pen (AbsoluteWithMouse ticked): a second virtual pen that KWin leaves absolute,
    // fed the raw tablet position with the tablet's own range and resolution, so KWin maps
    // the tablet to the screen like any real tablet (System Settings > Drawing Tablet can
    // change the mapping). Mouse mode works the same on top of it. The app cursor lock
    // doesn't apply to it: an absolute pen's position is the hand's, it can't be frozen.
    internal sealed class RelativePen : IRelativePointer, IPenActionHandler, IPressureHandler,
        ITiltHandler, IEraserHandler, ISynchronousPointer
    {
        public const string DeviceName = "OpenTabletDriver Relative Pen"; // "OpenTabletDriver*" = libinput quirk: smoothing off
        public const string MouseName = "OpenTabletDriver Relative Pen (Mouse Mode)";
        public const string AbsoluteName = "OpenTabletDriver Absolute Pen";

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

        private readonly EvdevDevice relPen;
        private EvdevDevice absPen;                     // created the first time absolute is ticked
        private Vector2 absMax;
        private bool absolute;
        private EvdevDevice device => absolute ? absPen : relPen;
        public Vector2 TabletPosition;                  // raw, from RelativePenMode.Transform
        private double x = RangePx / 2, y = RangePx / 2;
        private bool eraser;
        private EvdevDevice mouse;                      // created the first time mouse mode is allowed
        private Vector2 mouseRemainder;                 // REL is whole px; carry the fraction
        private bool mouseMode, touching, penDirty, mouseDirty;
        private int buttonsDown;

        public RelativePen()
        {
            var max = (int)(RangePx * UnitsPerPx);
            relPen = CreatePen(DeviceName, max, max, Resolution, Resolution);
            if (relPen == null)
                return;
            Task.Run(() => KdeRelative.SetRelative(DeviceName, true));
            CursorLock.Start();
            MouseModeKey.Start();
        }

        private static unsafe EvdevDevice CreatePen(string name, int maxX, int maxY, int resX, int resY)
        {
            var pen = new EvdevDevice(name);
            pen.EnableProperty(InputProperty.INPUT_PROP_POINTER);
            pen.EnableType(EventType.EV_ABS);

            var xAbs = new input_absinfo { maximum = maxX, resolution = resX };
            pen.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_X, (IntPtr)(&xAbs));
            var yAbs = new input_absinfo { maximum = maxY, resolution = resY };
            pen.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_Y, (IntPtr)(&yAbs));
            var pressure = new input_absinfo { maximum = MaxPressure };
            pen.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_PRESSURE, (IntPtr)(&pressure));
            var xTilt = new input_absinfo { minimum = -64, maximum = 63, resolution = 57 };
            pen.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_TILT_X, (IntPtr)(&xTilt));
            var yTilt = new input_absinfo { minimum = -64, maximum = 63, resolution = 57 };
            pen.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_TILT_Y, (IntPtr)(&yTilt));

            pen.EnableTypeCodes(EventType.EV_KEY, Keys);

            var result = pen.Initialize();
            if (result != ERRNO.NONE)
            {
                Log.WriteNotify("Relative Pen Mode", $"Failed to create virtual pen '{name}' (error {result}); is uinput loaded?", LogLevel.Error);
                return null;
            }
            Log.Write("Relative Pen Mode", $"Virtual pen '{name}' created");
            return pen;
        }

        private static readonly EventCode[] Keys =
        [
            EventCode.BTN_TOUCH, EventCode.BTN_STYLUS, EventCode.BTN_STYLUS2, EventCode.BTN_STYLUS3,
            EventCode.BTN_TOOL_PEN, EventCode.BTN_TOOL_RUBBER,
        ];

        private EventCode Tool => eraser ? EventCode.BTN_TOOL_RUBBER : EventCode.BTN_TOOL_PEN;

        // From RelativePenMode on OTD's thread, like every other call here. The current pen
        // leaves proximity; the next report brings the other one in.
        public void SetAbsolute(bool on, DigitizerSpecifications digitizer)
        {
            if (on && absPen == null && digitizer is { MaxX: > 0, MaxY: > 0, Width: > 0, Height: > 0 })
            {
                // The tablet's own units, so KWin sees the real size in mm.
                absMax = new Vector2(digitizer.MaxX, digitizer.MaxY);
                absPen = CreatePen(AbsoluteName, (int)digitizer.MaxX, (int)digitizer.MaxY,
                    (int)Math.Round(digitizer.MaxX / digitizer.Width), (int)Math.Round(digitizer.MaxY / digitizer.Height));
                if (absPen != null)
                    Task.Run(() => KdeRelative.SetRelative(AbsoluteName, false));
            }
            on &= absPen != null;
            if (on == absolute) return;
            if (!mouseMode) PenOut();
            absolute = on;
            touching = false;
            buttonsDown = 0;
            CursorLock.PenLeft();
            Log.Write("Relative Pen Mode", on ? "Absolute pen on" : "Absolute pen off");
        }

        public void SetMouseModeAllowed(bool on)
        {
            if (on && mouse == null)
                mouse = CreateMouse();
            MouseModeKey.SetAllowed(on && mouse != null);
        }

        public void SetPosition(Vector2 delta)
        {
            MouseModeKey.PenEntered();
            var wantMouse = MouseModeKey.Wanted && mouse != null;
            if (wantMouse != mouseMode && !touching && buttonsDown == 0)
            {
                mouseMode = wantMouse;
                if (mouseMode)
                {
                    PenOut();
                    CursorLock.PenLeft();   // ends an app's lock; no reports reach it while mouse
                }
            }
            if (mouseMode)
            {
                var d = delta + mouseRemainder;
                var whole = new Vector2(MathF.Truncate(d.X), MathF.Truncate(d.Y));
                mouseRemainder = d - whole;
                if (whole.X != 0) mouse.Write(EventType.EV_REL, EventCode.REL_X, (int)whole.X);
                if (whole.Y != 0) mouse.Write(EventType.EV_REL, EventCode.REL_Y, (int)whole.Y);
                mouseDirty |= whole != Vector2.Zero;
                return;
            }

            if (absolute)
            {
                device.Write(EventType.EV_KEY, Tool, 1);
                device.Write(EventType.EV_ABS, EventCode.ABS_X, (int)Math.Clamp(TabletPosition.X, 0, absMax.X));
                device.Write(EventType.EV_ABS, EventCode.ABS_Y, (int)Math.Clamp(TabletPosition.Y, 0, absMax.Y));
                penDirty = true;
                return;
            }

            delta = CursorLock.Divert(delta);   // zero while an app holds the cursor lock
            // Clamp is a backstop only: 4M px from centre is ~2 km of hand travel.
            x = Math.Clamp(x + delta.X, 0, RangePx);
            y = Math.Clamp(y + delta.Y, 0, RangePx);
            device.Write(EventType.EV_KEY, Tool, 1);
            device.Write(EventType.EV_ABS, EventCode.ABS_X, (int)Math.Round(x * UnitsPerPx));
            device.Write(EventType.EV_ABS, EventCode.ABS_Y, (int)Math.Round(y * UnitsPerPx));
            penDirty = true;
        }

        public void SetPressure(float percentage)
        {
            if (mouseMode) return;
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, (int)(MaxPressure * Math.Clamp(percentage, 0f, 1f)));
            penDirty = true;
        }

        public void SetTilt(Vector2 tilt)
        {
            if (mouseMode) return;
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_X, (int)tilt.X);
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_Y, (int)tilt.Y);
            penDirty = true;
        }

        public void SetEraser(bool isEraser)
        {
            if (eraser == isEraser)
                return;
            if (!mouseMode)
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
            // Mouse buttons mapped the way libinput maps pen buttons: STYLUS2 = right, STYLUS = middle.
            EventCode? code = (action, mouseMode) switch
            {
                (PenAction.Tip or PenAction.Eraser, false) => EventCode.BTN_TOUCH,
                (PenAction.BarrelButton1, false) => EventCode.BTN_STYLUS2,   // matches Artist Mode: button 1 = right click
                (PenAction.BarrelButton2, false) => EventCode.BTN_STYLUS,
                (PenAction.BarrelButton3, false) => EventCode.BTN_STYLUS3,
                (PenAction.Tip or PenAction.Eraser, true) => EventCode.BTN_LEFT,
                (PenAction.BarrelButton1, true) => EventCode.BTN_RIGHT,
                (PenAction.BarrelButton2, true) => EventCode.BTN_MIDDLE,
                (PenAction.BarrelButton3, true) => EventCode.BTN_SIDE,
                _ => null,
            };
            if (code is not { } c)
                return;
            if (action is PenAction.Tip or PenAction.Eraser)
                touching = state == 1;
            else
                buttonsDown = Math.Max(0, buttonsDown + (state == 1 ? 1 : -1));
            var dev = mouseMode ? mouse : device;
            dev.Write(EventType.EV_KEY, c, state);
            dev.Sync();
        }

        private void PenOut()
        {
            foreach (var key in Keys)
                device.Write(EventType.EV_KEY, key, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            device.Sync();
            penDirty = false;
        }

        // Pen left the tablet: go out of proximity and recentre. Safe to jump here —
        // KWin doesn't warp a relative tool on proximity-in, and libinput reports a
        // zero delta for the entering-proximity frame.
        // Mouse mode outlives the lift while the key is held: lifting is how a mouse repositions.
        public void Reset()
        {
            foreach (var key in Keys)
                device.Write(EventType.EV_KEY, key, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            penDirty = true;
            if (mouseMode)
            {
                foreach (var b in MouseButtons)
                    mouse.Write(EventType.EV_KEY, b, 0);
                mouseDirty = true;
            }
            eraser = touching = false;
            buttonsDown = 0;
            x = y = RangePx / 2;
            mouseRemainder = Vector2.Zero;
            CursorLock.PenLeft();
            MouseModeKey.PenLeft();
        }

        public void Flush()
        {
            if (penDirty) device.Sync();
            if (mouseDirty) mouse.Sync();
            penDirty = mouseDirty = false;
        }

        private static readonly EventCode[] MouseButtons =
            [EventCode.BTN_LEFT, EventCode.BTN_RIGHT, EventCode.BTN_MIDDLE, EventCode.BTN_SIDE];

        private static EvdevDevice CreateMouse()
        {
            var m = new EvdevDevice(MouseName);
            m.EnableTypeCodes(EventType.EV_REL, EventCode.REL_X, EventCode.REL_Y);
            m.EnableTypeCodes(EventType.EV_KEY, MouseButtons);
            var result = m.Initialize();
            if (result != ERRNO.NONE)
            {
                Log.WriteNotify("Relative Pen Mode", $"Failed to create the mouse-mode mouse (error {result})", LogLevel.Error);
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

        // KWin remembers this per device name; set every start in case the config was reset.
        public static void SetRelative(string deviceName, bool relative)
        {
            if (Find(deviceName) is not { } dev)
                return;
            var mode = relative ? "relative" : "absolute";
            var ok = Set(dev, "tabletToolIsRelative", "b", relative ? "true" : "false");
            Log.Write("Relative Pen Mode", ok
                ? $"KDE {mode} mode set on {dev}"
                : $"Could not set KDE {mode} mode on '{deviceName}'", ok ? LogLevel.Info : LogLevel.Warning);
        }

        // Mouse-mode mouse: flat acceleration at speed 0, so it moves 1:1 like the pen, whatever
        // the user's mouse settings are.
        public static void MouseFlat()
        {
            if (Find(RelativePen.MouseName) is not { } dev)
                return;
            var ok = Set(dev, "pointerAccelerationProfileFlat", "b", "true")
                && Set(dev, "pointerAcceleration", "d", "0");
            Log.Write("Relative Pen Mode", ok
                ? $"Mouse-mode mouse set to flat acceleration on {dev}"
                : "Could not set flat acceleration on the mouse-mode mouse", ok ? LogLevel.Info : LogLevel.Warning);
        }

        // Mouse mode needs KWin to hand the pen the mouse's position on proximity-in, or
        // strokes would start where the pen last was. A global KDE setting ([Tablet]
        // SyncWithMouse); KWin watches the file, so --notify applies it live.
        public static void EnsureSyncWithMouse()
        {
            if (Run("kreadconfig6", "--file", "kcminputrc", "--group", "Tablet", "--key", "SyncWithMouse")?.Trim() == "true")
                return;
            var ok = Run("kwriteconfig6", "--file", "kcminputrc", "--group", "Tablet", "--key", "SyncWithMouse", "true", "--notify") != null;
            Log.Write("Relative Pen Mode", ok
                ? "KDE 'sync tablet with mouse' turned on (needed by mouse mode)"
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

        public static string Busctl(params string[] args) => Run("busctl", ["--user", .. args]);

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
