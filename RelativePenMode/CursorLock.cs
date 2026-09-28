using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Threading;
using OpenTabletDriver.Plugin;
using OpenTabletDriver.Plugin.Attributes;
using OpenTabletDriver.Plugin.Output;
using OpenTabletDriver.Plugin.Tablet;

namespace RelativePenMode
{
    // The Filters-tab checkbox for the app cursor lock. OTD shows no custom settings on the
    // Output tab, so being ticked in the Filters tab IS the setting. Position None keeps it
    // out of the report pipeline entirely; RelativePenMode just looks for it in its elements.
    [PluginName("Relative Pen: Freeze Cursor Position on Supported App Sliders")]
    public class AppCursorLock : IPositionedPipelineElement<IDeviceReport>
    {
        public PipelinePosition Position => PipelinePosition.None;
        public event Action<IDeviceReport> Emit;
        public void Consume(IDeviceReport value) => Emit?.Invoke(value);
    }

    // Pointer lock for the pen. Wayland only lets an app lock a MOUSE (pointer-constraints);
    // the tablet protocol has no lock and no warp. So an app asks us instead: while locked,
    // the pen's hidden position stops moving (KWin's relative mode then keeps the cursor
    // still) and the motion goes to the app over the socket. On unlock the cursor is where
    // it was, because it never moved, and there's no screen edge in between.
    //
    // Protocol: docs in ~/Projects/CHISEL/otd-relative-pen-plugin.md ("App cursor lock").
    // SOCK_SEQPACKET at $XDG_RUNTIME_DIR/otd-relative-pen.sock, one ASCII message per packet:
    //   app -> us:  lock | unlock
    //   us -> app:  hello 1 | locked | denied off|no-pen|busy | motion <dx> <dy> |
    //               unlocked request|out-of-range|timeout|off
    //
    // A stuck lock would freeze the user's cursor, so every way out ends it: the app's
    // connection closing (crash included), the pen leaving range, the filter being unticked,
    // or MaxLock passing.
    internal static class CursorLock
    {
        public const string FileName = "otd-relative-pen.sock";
        public const int ProtocolVersion = 1;
        private static readonly TimeSpan MaxLock = TimeSpan.FromSeconds(60);
        // The tablet streams reports the whole time the pen is in range, even held still.
        // No report for this long = the pen is gone, or another output mode took over.
        private static readonly TimeSpan PenStale = TimeSpan.FromMilliseconds(250);

        private static readonly object gate = new();
        private static bool started, allowed, inRange;
        private static long lastReport, lockedAt;
        private static Client holder;

        public static void Start()
        {
            if (started) return;
            started = true;
            var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(dir))
            {
                Log.Write("Relative Pen Mode", "No XDG_RUNTIME_DIR; app cursor lock unavailable", LogLevel.Warning);
                return;
            }
            var path = Path.Combine(dir, FileName);
            new Thread(() => Listen(path)) { IsBackground = true, Name = "RelativePen lock" }.Start();
        }

        // From RelativePenMode on each report: is the Filters-tab checkbox ticked?
        public static void SetAllowed(bool value)
        {
            lock (gate)
            {
                if (allowed == value) return;
                allowed = value;
                if (!value) End("off");
            }
        }

        // From the pen, once per report, on OTD's thread. Returns the delta to apply to the
        // hidden position: zero while locked (the motion went to the lock holder instead).
        public static Vector2 Divert(Vector2 delta)
        {
            lock (gate)
            {
                inRange = true;
                lastReport = Stopwatch.GetTimestamp();
                if (holder == null) return delta;
                if (Stopwatch.GetElapsedTime(lockedAt) > MaxLock)
                {
                    End("timeout");
                    return delta;
                }
                holder.Motion(delta);
                return Vector2.Zero;
            }
        }

        // From the pen's Reset(): a real out-of-range report.
        public static void PenLeft()
        {
            lock (gate)
            {
                inRange = false;
                End("out-of-range");
            }
        }

        private static void Handle(Client c, string msg)
        {
            lock (gate)
            {
                switch (msg)
                {
                    case "lock":
                        if (holder == c) c.Send("locked");
                        // no-pen first: `allowed` is only learnt from a report
                        else if (!PenPresent()) c.Send("denied no-pen");
                        else if (!allowed) c.Send("denied off");
                        else if (holder != null) c.Send("denied busy");
                        else
                        {
                            holder = c;
                            lockedAt = Stopwatch.GetTimestamp();
                            c.Send("locked");
                        }
                        break;
                    case "unlock":
                        if (holder == c) End("request");
                        else c.Send("unlocked request");   // idempotent: the app's view ends up right either way
                        break;
                    default:
                        c.Send("error unknown-command");
                        break;
                }
            }
        }

        private static void Gone(Client c)
        {
            lock (gate)
                if (holder == c) holder = null;   // nobody left to tell
        }

        // Called about twice a second from each connection's thread, so a lock still ends
        // when no reports arrive at all (Divert never runs then).
        private static void Tick()
        {
            lock (gate)
            {
                if (holder == null) return;
                if (!PenPresent()) End("out-of-range");
                else if (Stopwatch.GetElapsedTime(lockedAt) > MaxLock) End("timeout");
            }
        }

        private static bool PenPresent() => inRange && Stopwatch.GetElapsedTime(lastReport) < PenStale;

        private static void End(string reason)
        {
            var c = holder;
            if (c == null) return;
            holder = null;
            c.Unsent = Vector2.Zero;   // held-back motion must not leak into the next lock
            c.Send($"unlocked {reason}");
        }

        private static void Listen(string path)
        {
            try
            {
                File.Delete(path);   // left over from a daemon that died; no-op if absent
                var listener = new Socket(AddressFamily.Unix, SocketType.Seqpacket, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(8);
                Log.Write("Relative Pen Mode", $"App cursor lock listening at {path}");
                while (true)
                {
                    var c = new Client(listener.Accept());
                    new Thread(c.Run) { IsBackground = true, Name = "RelativePen lock client" }.Start();
                }
            }
            catch (Exception e)
            {
                Log.Write("Relative Pen Mode", $"App cursor lock stopped: {e.Message}", LogLevel.Warning);
            }
        }

        private sealed class Client
        {
            private readonly Socket sock;
            public Vector2 Unsent;   // motion that didn't fit while the app wasn't reading; gate-guarded

            public Client(Socket sock)
            {
                this.sock = sock;
                // Never block OTD's report thread on a slow app: a full buffer just means
                // the motion is held back and sent summed with the next report's.
                sock.Blocking = false;
            }

            public void Run()
            {
                var buf = new byte[256];
                try
                {
                    Send($"hello {ProtocolVersion}");
                    while (true)
                    {
                        Tick();
                        if (!sock.Poll(500_000, SelectMode.SelectRead)) continue;
                        int n;
                        try { n = sock.Receive(buf); }
                        catch (SocketException e) when (e.SocketErrorCode == SocketError.WouldBlock) { continue; }
                        if (n == 0) break;   // app closed the connection
                        Handle(this, Encoding.ASCII.GetString(buf, 0, n).Trim());
                    }
                }
                catch (SocketException) { }   // reset by the app, or the app died
                catch (ObjectDisposedException) { }
                finally
                {
                    Gone(this);
                    sock.Dispose();
                }
            }

            // Caller holds the gate.
            public void Motion(Vector2 delta)
            {
                var d = Unsent + delta;
                Unsent = Send(string.Create(CultureInfo.InvariantCulture, $"motion {d.X:0.###} {d.Y:0.###}")) ? Vector2.Zero : d;
            }

            // Caller holds the gate. False = not sent (buffer full, or the app is gone).
            public bool Send(string msg)
            {
                try
                {
                    sock.Send(Encoding.ASCII.GetBytes(msg));
                    return true;
                }
                catch (SocketException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
        }
    }
}
