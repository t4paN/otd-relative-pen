using System;
using System.Diagnostics;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace RelativePenMode
{
    // Filters-tab switches for mouse on demand. Opt-in, because the key it grabs (Space by default)
    // stops typing while the pen is near the tablet. Position None: never in the report
    // pipeline, RelativePenMode just looks for them, like AppCursorLock. The settings file
    // stores the class names, so the display names can change freely.
    [PluginName("Relative Pen: Mouse on Demand (default Space)")]
    public class HoldForMouse : IPositionedPipelineElement<IDeviceReport>
    {
        public PipelinePosition Position => PipelinePosition.None;
        public event Action<IDeviceReport> Emit;
        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }

    // The pen goes absolute (tablet area = screen, like Artist Mode), with the same mouse on demand
    // on the shortcut. Implies HoldForMouse.
    [PluginName("Relative Pen: Absolute Pen + Mouse on Demand (default Space)")]
    public class AbsoluteWithMouse : IPositionedPipelineElement<IDeviceReport>
    {
        public PipelinePosition Position => PipelinePosition.None;
        public event Action<IDeviceReport> Emit;
        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }

    // Press once = mouse, press again = pen, instead of holding the key.
    [PluginName("Relative Pen: Mouse on Demand Toggles Instead of Hold")]
    public class ToggleMouse : IPositionedPipelineElement<IDeviceReport>
    {
        public PipelinePosition Position => PipelinePosition.None;
        public event Action<IDeviceReport> Emit;
        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }

    // Hold a key = the pen drives a plain mouse; let go = pen again. Or, with ToggleMouse,
    // each press flips it, and a KDE OSD says which one you're in. For apps that only want a
    // mouse (Chisel reads pressure from the tablet itself; browsers only pointer-lock a mouse).
    // Unlike the removed hover-as-mouse, the user decides when it flips, never the hover state.
    //
    // The key is a KDE global shortcut that we register ourselves, so it shows up in System
    // Settings > Shortcuts ("OpenTabletDriver Relative Pen") and can be rebound there. Only a
    // shortcut's own owner hears the key's RELEASE (kglobalaccel's globalShortcutReleased);
    // a System Settings "command" shortcut only fires on press, which can't do hold.
    //
    // A global shortcut eats the key everywhere, so it's live only while the pen is in range,
    // plus Grace after it leaves (lifting the pen to reposition is part of using a mouse), and
    // never switched off while held (the release would then go missing). A toggled-on mouse
    // mode ends with it, so a pen put away comes back as a pen.
    //
    // D-Bus via busctl/gdbus subprocesses, like KdeRelative, so the plugin stays one DLL.
    internal static class MouseOnDemandKey
    {
        private const string Svc = "org.kde.kglobalaccel", Accel = "org.kde.KGlobalAccel";
        // The action id predates the "Mouse on demand" name; changing it would orphan the user's binding.
        private const string Component = "otd-relative-pen", Action = "mouse-mode";
        private static readonly string[] ActionId = [Component, Action, "OpenTabletDriver Relative Pen", "Mouse on demand"];
        private const string DefaultKey = "32";            // Qt::Key_Space
        private const string IsDefault = "8", SetPresent = "2";   // KGlobalAccel::SetShortcutFlag
        private static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

        private static readonly object gate = new();
        private static bool started, allowed, toggle;
        private static volatile bool penIn, wanted;
        private static long penLeftAt;
        private static Process monitor;

        // True while mouse on demand is wanted (key held, or toggled on); read by the pen on OTD's thread.
        public static bool Wanted => wanted;

        public static void Start()
        {
            if (started) return;
            started = true;
            new Thread(Worker) { IsBackground = true, Name = "RelativePen mouse key" }.Start();
            new Thread(Listen) { IsBackground = true, Name = "RelativePen mouse key listener" }.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                SetInactive();
                try { monitor?.Kill(); } catch { }
            };
        }

        // From RelativePenMode when the settings change: is the Filters-tab entry ticked?
        public static void SetAllowed(bool value)
        {
            lock (gate)
            {
                if (allowed == value) return;
                allowed = value;
                if (!value) wanted = false;
                Monitor.PulseAll(gate);
            }
        }

        // From RelativePenMode when the settings change: toggle (true) or hold (false)?
        public static void SetToggle(bool value)
        {
            lock (gate)
            {
                if (toggle == value) return;
                toggle = value;
                wanted = false;
                Monitor.PulseAll(gate);
            }
        }

        // From the pen on every report; only the first one after a lift does anything.
        public static void PenEntered()
        {
            if (penIn) return;
            lock (gate)
            {
                penIn = true;
                Monitor.PulseAll(gate);
            }
        }

        // From the pen's Reset(): a real out-of-range.
        public static void PenLeft()
        {
            lock (gate)
            {
                penIn = false;
                penLeftAt = Stopwatch.GetTimestamp();
            }
        }

        // Keeps the shortcut live exactly when it should be. Subprocesses take a few ms, so
        // this never runs on OTD's report thread.
        private static void Worker()
        {
            SetInactive();   // a daemon that died with the key live would leave it grabbed
            bool registered = false, active = false;
            while (true)
            {
                bool want;
                lock (gate)
                {
                    want = allowed && (penIn || (wanted && !toggle) || Stopwatch.GetElapsedTime(penLeftAt) < Grace);
                    if (!want && toggle && wanted)
                    {
                        wanted = false;
                        Osd(false);
                    }
                    if (want == active)
                    {
                        Monitor.Wait(gate, 250);
                        continue;
                    }
                }
                if (want && !registered)
                {
                    // doRegister makes the action exist; IsDefault records Space as the default
                    // ("Reset to default" in System Settings) without making it live.
                    registered = Call("doRegister", "as", [.. Ids()]) != null
                        && Call("setShortcut", "asaiu", [.. Ids(), "1", DefaultKey, IsDefault]) != null;
                    if (!registered)
                    {
                        Log.Write("Relative Pen Mode", "KDE global shortcuts not reachable; mouse on demand key unavailable (KDE only)", LogLevel.Warning);
                        return;
                    }
                    Log.Write("Relative Pen Mode", "Mouse on demand key registered (default Space; rebind in System Settings > Shortcuts)");
                }
                // SetPresent without NoAutoloading: KDE keeps whatever key the user chose.
                var ok = want
                    ? Call("setShortcut", "asaiu", [.. Ids(), "1", DefaultKey, SetPresent]) != null
                    : SetInactive();
                if (ok)
                    active = want;
                else
                    Thread.Sleep(1000);
            }
        }

        // gdbus monitor prints one line per signal, unbuffered (3 ms from KDE to us).
        private static void Listen()
        {
            while (true)
            {
                try
                {
                    var psi = new ProcessStartInfo("gdbus") { RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in new[] { "monitor", "--session", "--dest", Svc, "--object-path", "/component/otd_relative_pen" })
                        psi.ArgumentList.Add(a);
                    using var p = Process.Start(psi);
                    monitor = p;
                    string line;
                    while ((line = p.StandardOutput.ReadLine()) != null)
                    {
                        if (!line.Contains($"('{Component}', '{Action}'")) continue;
                        if (line.Contains(".globalShortcutPressed"))
                            Key(KeyEvent.Pressed);
                        else if (line.Contains(".globalShortcutRepeated"))
                            Key(KeyEvent.Repeated);
                        else if (line.Contains(".globalShortcutReleased"))
                            Key(KeyEvent.Released);
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    Log.Write("Relative Pen Mode", "gdbus not found; mouse on demand key unavailable", LogLevel.Warning);
                    return;
                }
                catch (Exception e)
                {
                    Log.Write("Relative Pen Mode", $"Mouse on demand key listener failed: {e.Message}", LogLevel.Warning);
                }
                lock (gate) wanted = false;   // a release may have been missed; never leave mouse on demand stuck on
                Thread.Sleep(2000);
            }
        }

        private enum KeyEvent { Pressed, Repeated, Released }

        private static void Key(KeyEvent e)
        {
            lock (gate)
            {
                if (!allowed) return;
                if (!toggle)
                    wanted = e != KeyEvent.Released;
                else if (e == KeyEvent.Pressed)
                {
                    wanted = !wanted;
                    Osd(wanted);
                }
                Monitor.PulseAll(gate);
            }
        }

        // Plasma's on-screen display, the one volume changes use. Best effort.
        private static void Osd(bool mouse) => System.Threading.Tasks.Task.Run(() =>
            KdeRelative.Busctl("call", "org.kde.plasmashell", "/org/kde/osdService", "org.kde.osdService", "showText", "ss",
                mouse ? "input-mouse" : "input-tablet", mouse ? "Mouse on demand" : "Pen"));

        private static bool SetInactive() => Call("setInactive", "as", [.. Ids()]) != null;

        private static string[] Ids() => [ActionId.Length.ToString(), .. ActionId];

        private static string Call(string method, string signature, string[] args) =>
            KdeRelative.Busctl(["call", Svc, "/kglobalaccel", Accel, method, signature, .. args]);
    }
}
