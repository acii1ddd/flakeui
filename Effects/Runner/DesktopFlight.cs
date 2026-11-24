using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;

internal sealed class DesktopFlight
{
    private readonly Random random = new();
    private readonly List<(Point Point, double Distance)> nearby = new();
    private Task<Point[]>? scan;
    private Point[] icons = Array.Empty<Point>();
    private long nextScan;
    private Point start, target;
    private double elapsed, duration, baselineFlight, pause;
    private bool landingOnIcon;
    internal bool Perched => landingOnIcon && pause > 0;
    private int jumpsUntilCruise = 4;

    internal void Reset()
    {
        duration = pause = 0;
        nextScan = 0;
        icons = Array.Empty<Point>();
    }

    internal bool Advance(long now, double dt, RainSurface.WindowField windows, Size size, ref Point ship, RainPreferences? preferences)
    {
        if (scan is { IsCompleted: true })
        {
            icons = scan.IsCompletedSuccessfully ? scan.Result : Array.Empty<Point>();
            if (scan.IsFaulted) _ = scan.Exception;
            scan = null;
            nextScan = now + Stopwatch.Frequency;
        }
        if (scan is null && now >= nextScan)
            scan = Task.Run(ReadIcons);
        if (icons.Length == 0) return false;

        if (duration > 0)
        {
            elapsed = Math.Min(duration, elapsed + dt);
            double t = elapsed / duration;
            double curve = t * t * (3 - 2 * t);
            ship = start + (target - start) * curve;
            ship.Y = Math.Max(12, ship.Y - 42 * Math.Sin(Math.PI * t));
            if (elapsed >= duration)
            {
                pause = landingOnIcon ? RunnerScene.LandingPause(baselineFlight, .26) * (preferences?.LandingRest ?? 100) / 100 : .26;
                duration = 0;
            }
            return true;
        }

        pause -= dt;
        if (pause > 0 || preferences?.JumpSpeed == 0) return true;

        nearby.Clear();
        foreach (var icon in icons)
        {
            var point = windows.ToLocal((int)icon.X, (int)icon.Y);
            if (point.X < 12 || point.X > size.Width - 12 || point.Y < 12 || point.Y > size.Height - 8) continue;
            double distance = (point - ship).LengthSquared;
            if (distance > 144) nearby.Add((point, distance));
        }
        if (nearby.Count == 0 && jumpsUntilCruise > 0 && icons.Length > 1) return false;

        bool cruise = jumpsUntilCruise == 0 || nearby.Count == 0;
        landingOnIcon = !cruise;
        if (cruise)
        {
            jumpsUntilCruise = random.Next(3, 7);
            target = ChooseCruiseTarget(ship, size);
        }
        else
        {
            nearby.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
            target = nearby[random.Next(Math.Min(5, nearby.Count))].Point;
            jumpsUntilCruise--;
        }

        start = ship;
        elapsed = 0;
        double distanceToTarget = (target - start).Length;
        baselineFlight = cruise
            ? Math.Clamp(distanceToTarget / 255, 1.35, 3.35) * 1.08
            : Math.Clamp(distanceToTarget / 350, .76, 1.9) * 1.16 / .9;
        duration = cruise ? baselineFlight : baselineFlight * 100 / (preferences?.JumpSpeed ?? 100);
        return true;
    }

    private Point ChooseCruiseTarget(Point from, Size size)
    {
        double desiredDistance = Math.Min(size.Width * .29, 470);
        double bestDistance = -1;
        Point best = from;
        for (int i = 0; i < 5; i++)
        {
            var candidate = new Point(size.Width * (.17 + random.NextDouble() * .66),
                size.Height * (.16 + random.NextDouble() * .48));
            double distance = (candidate - from).Length;
            if (distance >= desiredDistance) return candidate;
            if (distance <= bestDistance) continue;
            best = candidate;
            bestDistance = distance;
        }
        return best;
    }

    internal static bool IsDesktopForeground()
    {
        nint handle = Native.GetForegroundWindow();
        if (handle == 0 || handle == Native.GetShellWindow() || handle == Native.GetDesktopWindow()) return true;
        var name = new System.Text.StringBuilder(32);
        GetClassName(handle, name, name.Capacity);
        return name.ToString() is "WorkerW" or "Progman";
    }

    internal static Point[] ReadIcons()
    {
        try
        {
            nint view = FindView(Native.FindWindow("Progman", null));
            for (nint host = 0; view == 0;)
            {
                host = FindWindowEx(0, host, "WorkerW", null);
                if (host == 0) return Array.Empty<Point>();
                view = FindView(host);
            }

            nint list = FindWindowEx(view, 0, "SysListView32", null);
            if (list == 0 || !Native.IsWindowVisible(list)) return Array.Empty<Point>();
            var root = AutomationElement.FromHandle(list);
            var items = root.FindAll(TreeScope.Children,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            var pins = new List<Point>(items.Count);
            foreach (AutomationElement item in items)
            {
                var rect = item.Current.BoundingRectangle;
                if (!rect.IsEmpty && rect.Width > 0 && rect.Height > 0)
                    pins.Add(new Point(rect.Left + rect.Width / 2, rect.Top - 5));
            }
            return pins.ToArray();
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return Array.Empty<Point>();
        }
    }

    private static nint FindView(nint host) => host == 0 ? 0 : FindWindowEx(host, 0, "SHELLDLL_DefView", null);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, System.Text.StringBuilder text, int capacity);
}
