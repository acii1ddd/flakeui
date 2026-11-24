using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

internal sealed class RainSurface : FrameworkElement
{
    private const int Count = 200;
    internal RainPreferences? Preferences { get; set; }
    private int liveCount;
    private double retireCredit;
    private const double MaxBounceHeight = 3.6, MergeDistance = 7, OverflowMass = 3.2;
    private const double ShakeAcceleration = 1700, MinEdgeSlope = .10, MaxEdgeSlope = .17;
    private readonly Raindrop[] drops = new Raindrop[Count];
    private readonly Random random = new();
    private readonly Pen tail = MakePen(Color.FromArgb(130, 46, 191, 255), 1.4);
    private readonly Pen glow = MakePen(Color.FromArgb(38, 53, 163, 255), 5);
    private readonly Pen spark = MakePen(Color.FromArgb(235, 225, 154, 255), 2);
    private TimeSpan? previous;
    private bool active;
    private bool light;
    private readonly WindowField windows;
    internal RainSurface(WindowField? windows = null) => this.windows = windows ?? new();
    private long previousCallback;
    private FrameSample? sample;

    internal FrameSample StartSample() => sample = new FrameSample();
    internal void StopSample() => sample = null;

    internal void SetMode(Mode mode)
    {
        active = mode is Mode.Rain or Mode.Light;
        light = mode == Mode.Light;
        previous = null;
        previousCallback = 0;
        retireCredit = 0;
        windows.Reset();
        Array.Clear(drops);
        InvalidateVisual();
    }

    internal void Advance(TimeSpan now, IntPtr overlay, Matrix fromDevice, bool invalidate = true, bool? refreshedSnapshot = null)
    {
        if (ActualWidth < 1 || ActualHeight < 1 || previous == now) return;
        long entered = Stopwatch.GetTimestamp();
        double dt = previous is { } prior ? (now - prior).TotalSeconds : 1.0 / 60;
        if (previousCallback != 0)
            sample?.Intervals.Add(Stopwatch.GetElapsedTime(previousCallback, entered).TotalMilliseconds);
        previousCallback = entered;
        var priorForDensity = previous;
        bool firstFrame = previous is null;
        previous = now;
        dt = Math.Clamp(dt, 0, .05) * .81 * ((Preferences?.Speed ?? 100) / 100.0);

        bool refreshed = refreshedSnapshot ?? windows.Refresh(entered, overlay, fromDevice, new Size(ActualWidth, ActualHeight));
        Point cursor = Native.GetCursorPos(out var mouse) ? windows.ToLocal(mouse.X, mouse.Y) : new Point(-10000, -10000);
        double damping = Math.Pow(.55, dt);

        int target = (light ? Count / 2 : Count) * (Preferences?.Intensity ?? 100) / 100;
        if (firstFrame || liveCount <= target)
        {
            liveCount = target;
            retireCredit = 0;
        }
        else
        {
            retireCredit += Math.Clamp((now - (priorForDensity ?? now)).TotalSeconds, 0, .05) * 64;
            int retire = Math.Min(liveCount - target, (int)retireCredit);
            Array.Clear(drops, liveCount - retire, retire);
            liveCount -= retire;
            retireCredit -= retire;
        }
        int count = liveCount;
        int resting = 0;
        for (int i = 0; i < count; i++) if (drops[i].State == Water.Rest) resting++;
        for (int i = 0; i < count; i++)
        {
            ref var drop = ref drops[i];
            if (drop.State == Water.Empty || drop.Y > ActualHeight + 35 || drop.X < -40 || drop.X > ActualWidth + 40)
            {
                bool initial = drop.State == Water.Empty && firstFrame;
                drop = new Raindrop
                {
                    X = random.NextDouble() * ActualWidth,
                    Y = initial ? random.NextDouble() * ActualHeight : -random.NextDouble() * 140,
                    Speed = 155 + random.NextDouble() * 340,
                    State = Water.Fall,
                    Mass = .75 + random.NextDouble() * .65,
                    Vx = 32 + random.NextDouble() * 20
                };
            }
            double dx = drop.X - cursor.X;
            double dy = drop.Y - cursor.Y;
            double distanceSquared = dx * dx + dy * dy;
            if (drop.Platform == IntPtr.Zero && distanceSquared is > .01 and < 10000)
            {
                double distance = Math.Sqrt(distanceSquared);
                drop.Vx += dx / distance * (100 - distance) * 8 * dt;
            }

            drop.Spark = Math.Max(0, drop.Spark - dt);
            if (drop.Platform != IntPtr.Zero)
            {
                var before = drop.State;
                windows.AdvanceAttached(ref drop, dt, refreshed);
                if (before == Water.Rest && drop.State != Water.Rest) resting--;
                if (before == Water.Bounce && drop.State == Water.Rest)
                {
                    int neighbor = -1;
                    for (int j = 0; j < count; j++)
                        if (j != i && drops[j].State == Water.Rest && drops[j].Platform == drop.Platform &&
                            Math.Abs(drops[j].PlatformX - drop.PlatformX) <= MergeDistance)
                        { neighbor = j; break; }
                    if (neighbor >= 0)
                    {
                        ref var bead = ref drops[neighbor];
                        bead.Mass += drop.Mass;
                        if (bead.Mass >= OverflowMass)
                        {
                            drop.Mass = bead.Mass - 1;
                            bead.Mass = 1;
                            drop.X = bead.X;
                            WindowField.Release(ref drop, 0, 20);
                        }
                        else drop = default;
                    }
                    else if (resting >= count / 4) WindowField.Release(ref drop, 0, 18);
                    else resting++;
                }
                continue;
            }
            const double wind = 42;
            drop.Vx = wind + (drop.Vx - wind) * damping;
            drop.Speed = Math.Min(595, drop.Speed + 295 * dt);
            double oldX = drop.X, oldY = drop.Y;
            windows.Move(ref drop, dt, refreshed);
            if (!drop.Hidden) Pond?.Crossing(oldX, oldY, drop.X, drop.Y);
        }
        if (invalidate) InvalidateVisual();
        sample?.Work.Add(Stopwatch.GetElapsedTime(entered).TotalMilliseconds);
    }

    internal NightPond? Pond { get; set; }
    internal bool HasVisibleDrops => drops.Any(drop => drop.State != Water.Empty && !drop.Hidden && drop.Y >= 0 && drop.Y < ActualHeight);

    internal void DrawNative(DirectRain renderer, float scale)
    {
        foreach (var drop in drops)
        {
            if (drop.State == Water.Empty || drop.Hidden || drop.Y < 0) continue;
            if (drop.IsBead)
            {
                renderer.RainBead((float)drop.X * scale, (float)drop.Y * scale, (float)drop.Radius * scale);
                continue;
            }
            renderer.Line((float)drop.X, (float)drop.Y,
                (float)(drop.X - drop.Vx * .030), (float)(drop.Y - drop.Speed * .030),
                drop.Spark > 0, light, scale);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (!active) return;
        foreach (var drop in drops)
        {
            if (drop.State == Water.Empty || drop.Hidden || drop.Y < 0) continue;
            if (drop.IsBead)
            {
                dc.DrawEllipse(tail.Brush, null, new Point(drop.X, drop.Y), drop.Radius, drop.Radius * .57);
                continue;
            }
            var tip = new Point(drop.X, drop.Y);
            var end = new Point(drop.X - drop.Vx * .030, drop.Y - drop.Speed * .030);
            if (!light) dc.DrawLine(glow, tip, end);
            dc.DrawLine(drop.Spark > 0 ? spark : tail, tip, end);
        }
    }

    private static Pen MakePen(Color color, double width)
    {
        var pen = new Pen(new SolidColorBrush(color), width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        return pen;
    }


    internal sealed class WindowField
    {
        private const double MinimumVisibleRoof = 4;
        private readonly List<Platform> platforms = new(24);
        private Dictionary<IntPtr, Rect> previous = new();
        private Dictionary<IntPtr, Rect> current = new();
        private readonly Native.WindowCallback collect;
        private IntPtr overlay, shell, desktop;
        private Matrix fromScreen;
        private Rect screen;
        private long lastRefresh;
        private double interval;
        private readonly Dictionary<nint, Vector> lastVelocities = new();

        internal WindowField() => collect = Collect;

        // Called by the dispatcher only after the background scan finishes.
        internal void CopySnapshot(WindowField source)
        {
            fromScreen = source.fromScreen;
            screen = source.screen;
            interval = source.interval;
            platforms.Clear();
            platforms.AddRange(source.platforms);
            previous.Clear();
            foreach (var platform in platforms) previous[platform.Handle] = platform.Bounds;
        }

        internal void Reset()
        {
            platforms.Clear();
            previous.Clear();
            current.Clear();
            lastRefresh = 0;
        }

        internal bool Refresh(long now, IntPtr handle, Matrix transform, Size size)
        {
            if (lastRefresh != 0 && Stopwatch.GetElapsedTime(lastRefresh, now).TotalMilliseconds < 28)
                return false;
            interval = lastRefresh == 0 ? 0 : Stopwatch.GetElapsedTime(lastRefresh, now).TotalSeconds;
            lastVelocities.Clear();
            foreach (var p in platforms) lastVelocities[p.Handle] = p.Velocity;
            if (screen.Size != size || fromScreen.M11 != transform.M11) interval = 0;
            lastRefresh = now;
            overlay = handle;
            shell = Native.GetShellWindow();
            desktop = Native.GetDesktopWindow();
            var origin = new Native.POINT();
            Native.ClientToScreen(overlay, ref origin);
            fromScreen = transform;
            fromScreen.Translate(-origin.X * transform.M11, -origin.Y * transform.M22);
            screen = new Rect(size);
            platforms.Clear();
            current.Clear();
            Native.EnumWindows(collect, IntPtr.Zero);
            (previous, current) = (current, previous);
            return true;
        }

        private bool Collect(IntPtr handle, IntPtr unused)
        {
            if (handle == overlay || handle == shell || handle == desktop
                || !Native.IsWindowVisible(handle) || Native.IsIconic(handle)
                || (Native.GetWindowLong(handle, -20) & 0x80) != 0
                || (Native.GetCloaked(handle, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                || Native.DwmGetWindowAttribute(handle, 9, out var rect, 16) != 0
                || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                return true;

            var bounds = new Rect(fromScreen.Transform(new Point(rect.Left, rect.Top)),
                fromScreen.Transform(new Point(rect.Right, rect.Bottom)));
            if (!bounds.IntersectsWith(screen)) return true;
            current[handle] = bounds;
            Rect old = previous.GetValueOrDefault(handle, bounds);
            Vector velocity = default, acceleration = default, prior = lastVelocities.GetValueOrDefault(handle);
            Vector delta = bounds.TopLeft - old.TopLeft;
            bool tracking = interval is > 0 and < .16 && bounds.Size == old.Size && delta.Length < 110 && previous.ContainsKey(handle);
            if (tracking)
            {
                if (delta.Length < 1) delta = default;
                velocity = .5 * prior + .5 * delta / interval;
                acceleration = (velocity - prior) / interval;
            }
            platforms.Add(new Platform(handle, bounds, tracking ? old : bounds, velocity, acceleration));
            return true;
        }

        internal Point ToLocal(int x, int y) => fromScreen.Transform(new Point(x, y));

        internal static void Release(ref Raindrop drop, double vx, double vy)
        {
            drop.Ignore = drop.Platform;
            drop.Platform = 0;
            drop.State = Water.Fall;
            drop.Vx = vx;
            drop.Speed = vy;
        }

        internal void AdvanceAttached(ref Raindrop drop, double dt, bool refreshed)
        {
            for (int i = 0; i < platforms.Count; i++)
            {
                var p = platforms[i];
                if (p.Handle != drop.Platform) continue;
                var b = p.Bounds;
                drop.PlatformX = Math.Clamp(drop.PlatformX, 0, b.Width);
                drop.X = b.Left + drop.PlatformX;
                drop.Y = b.Top - 1;
                if (drop.State == Water.Bounce)
                {
                    drop.Age += dt;
                    double t = Math.Min(1, drop.Age / drop.Duration);
                    drop.PlatformX += drop.Vx * dt;
                    drop.Vx *= Math.Exp(-18 * dt);
                    drop.Y -= 4 * drop.Height * t * (1 - t);
                    if (t >= 1) { drop.State = Water.Rest; drop.Vx = drop.Speed = 0; }
                }
                else if (drop.State == Water.Rest && refreshed)
                {
                    double force = p.Acceleration.Length * Math.Sqrt(drop.Mass);
                    if (force > ShakeAcceleration)
                    {
                        Vector inertia = p.Velocity - p.Acceleration * interval;
                        drop.Y = b.Top + 1;
                        Release(ref drop, Math.Clamp(inertia.X, -210, 210), Math.Clamp(inertia.Y + 15, -55, 150));
                    }
                    else if (force > ShakeAcceleration * .43)
                        drop.PlatformX -= Math.Clamp(p.Acceleration.X * interval * .06, -2, 2);
                }
                if (drop.State == Water.Rest && Math.Min(drop.PlatformX, b.Width - drop.PlatformX) <= drop.Radius + 1)
                {
                    drop.State = Water.Edge;
                    drop.Side = drop.PlatformX < b.Width / 2 ? -1 : 1;
                    drop.SideTravel = 0;
                    drop.Speed = 8;
                }
                if (drop.State == Water.Edge)
                {
                    drop.Speed += 180 * dt;
                    drop.SideTravel += drop.Speed * dt;
                    drop.X = (drop.Side < 0 ? b.Left : b.Right) + drop.Side * (1 + drop.Slope * drop.SideTravel);
                    drop.Y = b.Top + drop.SideTravel;
                    drop.Vx = drop.Side * drop.Slope * drop.Speed;
                    if (drop.SideTravel >= Math.Min(b.Height, drop.ReleaseDepth))
                        Release(ref drop, drop.Vx + p.Velocity.X, drop.Speed + p.Velocity.Y);
                }
                drop.Hidden = !Exposed(i, drop.X, drop.Y) || !Exposed(i, Math.Clamp(drop.X,b.Left,b.Right),b.Top);
                return;
            }
            drop = default;
        }

        internal void Move(ref Raindrop drop, double dt, bool refreshed)
        {
            if (refreshed) PushByMovingWindow(ref drop);
            drop.Hidden = false;
            if (drop.Ignore != 0 && (!previous.TryGetValue(drop.Ignore, out var owner) || drop.Y > owner.Top + 4))
                drop.Ignore = 0;
            double nextX = drop.X + drop.Vx * dt;
            double nextY = drop.Y + drop.Speed * dt;
            double first = double.PositiveInfinity;
            int hit = -1;
            for (int i = 0; i < platforms.Count; i++)
            {
                var platform = platforms[i];
                var b = platform.Bounds;
                if (platform.Handle != drop.Ignore && HasVisibleRoof(b) && drop.Y < b.Top && nextY >= b.Top)
                {
                    double t = (b.Top - drop.Y) / (nextY - drop.Y);
                    double x = drop.X + (nextX - drop.X) * t;
                    if (t < first && x >= b.Left && x <= b.Right && Exposed(i, x, b.Top))
                    {
                        first = t;
                        hit = i;
                    }
                }


            }

            if (hit >= 0)
            {
                var platform = platforms[hit];
                double impact = Math.Clamp(drop.X + drop.Vx * dt * first, platform.Bounds.Left, platform.Bounds.Right);
                drop.Platform = platform.Handle;
                drop.PlatformX = impact - platform.Bounds.Left;
                drop.Side = 0;
                double impactSpeed = Math.Clamp((drop.Speed - platform.Velocity.Y) / 550, 0, 1);
                drop.State = Water.Bounce;
                drop.Height = .25 + (MaxBounceHeight - .25) * impactSpeed * impactSpeed;
                drop.Duration = .08 + .08 * impactSpeed;
                drop.Age = 0;
                drop.ReleaseDepth = 15 + Random.Shared.NextDouble() * 15;
                drop.Slope = MinEdgeSlope + Random.Shared.NextDouble() * (MaxEdgeSlope - MinEdgeSlope);
                drop.Vx = Math.Clamp((drop.Vx - platform.Velocity.X) * .22, -22, 22);
                drop.Speed = 0;
                drop.Spark = .22;
                drop.X = impact;
                drop.Y = platform.Bounds.Top - 1;
                return;
            }
            drop.X = nextX;
            drop.Y = nextY;
        }

        private void PushByMovingWindow(ref Raindrop drop)
        {
            for (int i = 0; i < platforms.Count; i++)
            {
                var platform = platforms[i];
                var b = platform.Bounds;
                var old = platform.OldBounds;
                if (b != old && b.Contains(drop.X, drop.Y)
                    && !old.Contains(drop.X, drop.Y) && Exposed(i, drop.X, drop.Y))
                {
                    double push = b.Left - old.Left;
                    if (Math.Abs(push) >= 1)
                    {
                        drop.Vx = Math.Clamp(drop.Vx + push * 15, -460, 460);
                    }
                    double rise = old.Top - b.Top;
                    if (rise >= 1)
                        drop.Speed = Math.Max(-260, drop.Speed - rise * 15);
                    drop.Spark = .17;
                    return;
                }
            }
        }

        private bool HasVisibleRoof(Rect bounds) => bounds.Top >= screen.Top + MinimumVisibleRoof;

        private bool Exposed(int index, double x, double y)
        {
            for (int i = 0; i < index; i++)
                if (platforms[i].Bounds.Contains(x, y)) return false;
            return true;
        }

        internal IReadOnlyList<Platform> Platforms => platforms;

        internal readonly record struct Platform(IntPtr Handle, Rect Bounds, Rect OldBounds, Vector Velocity, Vector Acceleration);
    }

    internal enum Water { Empty, Fall, Bounce, Rest, Edge }

    internal struct Raindrop
    {
        internal double X, Y, Vx, Speed, Spark, PlatformX;
        internal Water State;
        internal bool Hidden;
        internal bool IsBead => State is Water.Rest or Water.Bounce;
        internal double Radius => Math.Sqrt(Mass) * (State == Water.Bounce && Age < .035 ? 2.2 : 1.6);
        internal double Mass, Age, Duration, Height, Slope;
        internal nint Ignore;
        internal int Side;
        internal double SideTravel, ReleaseDepth;
        internal IntPtr Platform;
    }
}
