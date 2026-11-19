using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

internal sealed class RunnerScene
{
    private readonly RainSurface.WindowField windows;
    internal RunnerScene(RainSurface.WindowField? windows = null) => this.windows = windows ?? new();
    private readonly List<Roof> roofs = new();
    private readonly NeonFrame frame = new();
    private readonly DesktopFlight desktopFlight = new();
    private bool desktopActive;
    private nint flashWindow;
    internal int FrameStrokes { get; private set; }
    private Size size;
    private nint attached, destination;
    private Point ship, takeoff, landing;
    internal RainPreferences? Preferences { get; set; }
    private double baselineFlight;
    private double progress, speed, hold, anchor = .5;
    private bool initialized, jumping;
    private readonly Random random = new();
    private int localHopsRemaining;
    private double patrolTime;
    private Point patrolOrigin;
    internal double PatrolDistance { get; private set; }

    internal bool IsJumping => jumping;
    internal int Transfers { get; private set; }
    internal int LocalHops { get; private set; }
    internal int RoofCount => roofs.Count;

    internal void Advance(long now, double dt, nint overlay, Matrix transform, Size viewport, bool? refreshedSnapshot = null)
    {
        size = viewport;
        bool refreshed = refreshedSnapshot ?? windows.Refresh(now, overlay, transform, size);
        if (refreshed)
        {
            RefreshRoofs();
            bool visibleDesktop = roofs.Count == 0 && (windows.Platforms.Count == 0 || DesktopFlight.IsDesktopForeground());
            if (desktopActive && !visibleDesktop) desktopFlight.Reset();
            desktopActive = visibleDesktop;
        }
        frame.Advance(windows, size, flashWindow, refreshed, dt, trackCursor: false);
        if (!initialized)
        {
            ship = new Point(size.Width * .22, size.Height * .25);
            initialized = true;
        }

        if (roofs.Count == 0)
        {
            if (patrolTime == 0) patrolOrigin = ship;
            attached = destination = 0;
            jumping = false;
            hold = 0;
            if (desktopActive && desktopFlight.Advance(now, dt, windows, size, ref ship, Preferences))
            {
                patrolTime = 0;
                return;
            }
            patrolTime += dt * .92;
            double blend = Math.Min(1, patrolTime / .7);
            double x = size.Width * (.5 + .28 * Math.Sin(patrolTime * .8));
            double y = size.Height * (.27 + .05 * Math.Sin(patrolTime * 1.6));
            ship = new Point(patrolOrigin.X + (x - patrolOrigin.X) * blend,
                patrolOrigin.Y + (y - patrolOrigin.Y) * blend);
            PatrolDistance = Math.Max(PatrolDistance, (ship - patrolOrigin).Length);
            return;
        }
        patrolTime = 0;

        if (jumping)
        {
            if (FindRoof(destination, landing.X) is { } target)
                landing = RoofPoint(target, anchor);
            else if (destination != 0)
            {
                jumping = false;
                hold = 0;
                SelectJump();
                return;
            }

            progress = Math.Min(1, progress + dt * speed);
            double arc = Math.Sin(Math.PI * progress) * 62;
            ship = new Point(takeoff.X + (landing.X - takeoff.X) * progress,
                Math.Max(12, takeoff.Y + (landing.Y - takeoff.Y) * progress - arc));
            if (progress >= 1)
            {
                if (attached == destination)
                {
                    LocalHops++;
                }
                else
                {
                    if (attached != 0) Transfers++;
                    localHopsRemaining = random.Next(3);
                }
                attached = destination;
                flashWindow = destination;
                frame.Land(destination, ship.X, ship.Y + 9);
                jumping = false;
                hold = LandingPause(baselineFlight, .32) * (Preferences?.LandingRest ?? 100) / 100;
            }
            return;
        }

        if (FindRoof(attached, ship.X) is { } current)
            ship = RoofPoint(current, anchor);
        hold -= dt;
        if (hold > 0) return;
        SelectJump();
    }

    // Two successive 20% frequency reductions scale the original cycle by 1 / (0.8 * 0.8).
    internal static double LandingPause(double flight, double oldPause) => (flight + oldPause) / (.8 * .8) - flight;

    private void SelectJump()
    {
        Roof? best = null;
        double cost = double.PositiveInfinity;
        foreach (var roof in roofs)
        {
            if (roof.Handle == attached) continue;
            var point = RoofPoint(roof, .5);
            double distance = Math.Abs(point.X - ship.X) + 1.2 * Math.Abs(point.Y - ship.Y);
            if (distance >= cost) continue;
            best = roof;
            cost = distance;
        }

        if (FindRoof(attached, ship.X) is { } current && (best is null || localHopsRemaining > 0))
        {
            if (best is not null) localHopsRemaining--;
            anchor = anchor > .5 ? .23 : .77;
            Jump(current.Handle, RoofPoint(current, anchor));
        }
        else if (best is { } candidate)
        {
            anchor = .5;
            Jump(candidate.Handle, RoofPoint(candidate, anchor));
        }
    }

    private void Jump(nint handle, Point point)
    {
        takeoff = ship;
        landing = point;
        destination = handle;
        progress = 0;
        baselineFlight = Math.Clamp((point - ship).Length / 400, .74, 2.0) / .9;
        speed = (Preferences?.JumpSpeed ?? 100) / (100 * baselineFlight);
        jumping = true;
    }

    private void RefreshRoofs()
    {
        roofs.Clear();
        var platforms = windows.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            var platform = platforms[i];
            var box = platform.Bounds;
            if (box.Top < 20 || box.Top >= size.Height) continue;
            double cursor = Math.Max(0, box.Left);
            double end = Math.Min(size.Width, box.Right);
            while (cursor < end)
            {
                double visibleUntil = end;
                double coveredUntil = cursor;
                for (int j = 0; j < i; j++)
                {
                    var above = platforms[j].Bounds;
                    if (above.Top > box.Top || above.Bottom <= box.Top || above.Right <= cursor) continue;
                    if (above.Left <= cursor) coveredUntil = Math.Max(coveredUntil, above.Right);
                    else visibleUntil = Math.Min(visibleUntil, above.Left);
                }
                if (coveredUntil > cursor)
                {
                    cursor = coveredUntil;
                    continue;
                }
                if (visibleUntil - cursor >= 28)
                    roofs.Add(new Roof(platform.Handle, cursor, visibleUntil, box.Top));
                cursor = visibleUntil;
            }
        }
    }

    private Roof? FindRoof(nint handle, double x)
    {
        Roof? nearest = null;
        double gap = double.PositiveInfinity;
        foreach (var roof in roofs)
        {
            if (roof.Handle != handle) continue;
            double distance = Math.Abs(x - Math.Clamp(x, roof.Left, roof.Right));
            if (distance >= gap) continue;
            nearest = roof;
            gap = distance;
        }
        return nearest;
    }

    private static Point RoofPoint(Roof roof, double fraction) =>
        new(roof.Left + (roof.Right - roof.Left) * fraction, roof.Top - 9);

    internal void Draw(DirectRain gpu, float scale, double seconds)
    {
        frame.Draw(gpu, scale, seconds, landingOnly: true);
        if (frame.HasLandingFlash) FrameStrokes = Math.Max(FrameStrokes, frame.StrokeCount);
        double x = ship.X, y = ship.Y;
        if (attached == 0 && !jumping && !(desktopActive && desktopFlight.Perched)) y += Math.Sin(seconds * 4) * 4;
        double direction = jumping && landing.X < takeoff.X ? -1 : 1;
        Line(x + 12 * direction, y, x - 8 * direction, y - 5);
        Line(x + 12 * direction, y, x - 8 * direction, y + 5);
        Line(x - 8 * direction, y - 5, x - 5 * direction, y);
        Line(x - 5 * direction, y, x - 8 * direction, y + 5);
        double tail = 12 + 4 * Math.Sin(seconds * 30);
        Line(x - 8 * direction, y, x - (8 + tail) * direction, y, true);

        void Line(double x1, double y1, double x2, double y2, bool spark = false) =>
            gpu.Line((float)x1, (float)y1, (float)x2, (float)y2, spark, false, scale);
    }

    private readonly record struct Roof(nint Handle, double Left, double Right, double Top);
}
