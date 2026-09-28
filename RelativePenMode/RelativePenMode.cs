using System;
using System.Diagnostics;
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
    }

    // Deliberately NOT an IMouseButtonHandler, so Adaptive Bindings route the tip and
    // barrel buttons to pen actions instead of mouse clicks.
    internal sealed class RelativePen : IRelativePointer, IPenActionHandler, IPressureHandler,
        ITiltHandler, IEraserHandler, ISynchronousPointer
    {
        public const string DeviceName = "OpenTabletDriver Relative Pen"; // "OpenTabletDriver*" = libinput quirk: smoothing off

        // 1 px of accumulator = UnitsPerPx device units; libinput turns units into mm via
        // Resolution, then mm into logical px via * 96/25.4*2.5. 254/2400 mm * 9.449 = 1.000,
        // so OTD's sensitivity means the same here as in stock Relative Mode.
        private const int UnitsPerPx = 254;
        private const int Resolution = 2400;           // units per mm
        private const double RangePx = 4_000_000;       // ~1e9 units, well inside int32
        private const int MaxPressure = ushort.MaxValue;

        private readonly EvdevDevice device;
        private double x = RangePx / 2, y = RangePx / 2;
        private bool eraser;

        public unsafe RelativePen()
        {
            device = new EvdevDevice(DeviceName);
            device.EnableProperty(InputProperty.INPUT_PROP_POINTER);
            device.EnableType(EventType.EV_ABS);

            var xAbs = new input_absinfo { maximum = (int)(RangePx * UnitsPerPx), resolution = Resolution };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_X, (IntPtr)(&xAbs));
            var yAbs = new input_absinfo { maximum = (int)(RangePx * UnitsPerPx), resolution = Resolution };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_Y, (IntPtr)(&yAbs));
            var pressure = new input_absinfo { maximum = MaxPressure };
            device.EnableCustomCode(EventType.EV_ABS, EventCode.ABS_PRESSURE, (IntPtr)(&pressure));
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

        public void SetPosition(Vector2 delta)
        {
            // Clamp is a backstop only: 4M px from centre is ~2 km of hand travel.
            x = Math.Clamp(x + delta.X, 0, RangePx);
            y = Math.Clamp(y + delta.Y, 0, RangePx);
            device.Write(EventType.EV_KEY, Tool, 1);
            device.Write(EventType.EV_ABS, EventCode.ABS_X, (int)Math.Round(x * UnitsPerPx));
            device.Write(EventType.EV_ABS, EventCode.ABS_Y, (int)Math.Round(y * UnitsPerPx));
        }

        public void SetPressure(float percentage) =>
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, (int)(MaxPressure * Math.Clamp(percentage, 0f, 1f)));

        public void SetTilt(Vector2 tilt)
        {
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_X, (int)tilt.X);
            device.Write(EventType.EV_ABS, EventCode.ABS_TILT_Y, (int)tilt.Y);
        }

        public void SetEraser(bool isEraser)
        {
            if (eraser == isEraser)
                return;
            device.Write(EventType.EV_KEY, Tool, 0);   // old tool leaves before the new one enters
            device.Sync();
            eraser = isEraser;
        }

        // Touch comes from the Tip/Eraser bindings (not pressure > 0) so OTD's tip
        // activation threshold still applies.
        public void Activate(PenAction action) => SetAction(action, 1);
        public void Deactivate(PenAction action) => SetAction(action, 0);

        private void SetAction(PenAction action, int state)
        {
            EventCode? code = action switch
            {
                PenAction.Tip or PenAction.Eraser => EventCode.BTN_TOUCH,
                PenAction.BarrelButton1 => EventCode.BTN_STYLUS2,   // matches Artist Mode: button 1 = right click
                PenAction.BarrelButton2 => EventCode.BTN_STYLUS,
                PenAction.BarrelButton3 => EventCode.BTN_STYLUS3,
                _ => null,
            };
            if (code is { } c)
            {
                device.Write(EventType.EV_KEY, c, state);
                device.Sync();
            }
        }

        // Pen left the tablet: go out of proximity and recentre. Safe to jump here —
        // KWin doesn't warp a relative tool on proximity-in, and libinput reports a
        // zero delta for the entering-proximity frame.
        public void Reset()
        {
            foreach (var key in Keys)
                device.Write(EventType.EV_KEY, key, 0);
            device.Write(EventType.EV_ABS, EventCode.ABS_PRESSURE, 0);
            eraser = false;
            x = y = RangePx / 2;
        }

        public void Flush() => device.Sync();
    }

    // Turns on KWin's relative mode for our pen, so installing the plugin is the whole
    // setup. KWin persists it by device name, and it's re-applied each daemon start in
    // case the user's config was reset. Uses busctl (ships with systemd) rather than a
    // D-Bus library, so the plugin stays a single DLL. Non-KDE desktops: logs and gives up.
    internal static class KdeRelative
    {
        private const string Svc = "org.kde.KWin", Root = "/org/kde/KWin/InputDevice";

        public static void Enable()
        {
            // The device shows up in KWin a moment after uinput creates it.
            for (int attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(250);
                var list = Busctl("get-property", Svc, Root, "org.kde.KWin.InputDeviceManager", "devicesSysNames");
                if (list == null)
                {
                    if (attempt == 0) continue;
                    Log.Write("Relative Pen Mode", "KWin not reachable; set relative mode for the pen manually (KDE only)", LogLevel.Warning);
                    return;
                }
                foreach (Match m in Regex.Matches(list, "\"([^\"]+)\""))
                {
                    var path = $"{Root}/{m.Groups[1].Value}";
                    var name = Busctl("get-property", Svc, path, "org.kde.KWin.InputDevice", "name");
                    if (name == null || !name.Contains($"\"{RelativePen.DeviceName}\""))
                        continue;
                    var ok = Busctl("set-property", Svc, path, "org.kde.KWin.InputDevice", "tabletToolIsRelative", "b", "true") != null;
                    Log.Write("Relative Pen Mode", ok
                        ? $"KDE relative mode enabled on {m.Groups[1].Value}"
                        : "Could not enable KDE relative mode", ok ? LogLevel.Info : LogLevel.Warning);
                    return;
                }
            }
            Log.Write("Relative Pen Mode", "Virtual pen never appeared in KWin; relative mode not set", LogLevel.Warning);
        }

        private static string Busctl(params string[] args)
        {
            try
            {
                var psi = new ProcessStartInfo("busctl") { RedirectStandardOutput = true, RedirectStandardError = true };
                psi.ArgumentList.Add("--user");
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
