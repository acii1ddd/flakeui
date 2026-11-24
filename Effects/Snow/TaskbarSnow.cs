using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

internal sealed class TaskbarSnow
{
    // Heights and widths are device-independent pixels. Delay is in seconds.
    private const double GapSheddingThreshold = 4.1;
    private const double EdgeSheddingThreshold = 1.95;
    private const double MinimumSheddingDelay = 2.5;
    private const double MaximumSheddingDelay = 6.5;
    private const double ShedFraction = .64;
    private const double MinimumGapWidth = 7;
    private const double GapMaxHeight = 9.5;
    private const double EdgeMaxHeight = 3.6;
    private const double EdgeSectionWidth = 64;
    private const double SweepSpeed = 380; // Horizontal DIPs per second.
    private const double SheddingDuration = .65;

    private readonly Random random;
    private readonly Grain[] grains = new Grain[96];
    private int nextGrain;
    private Drift[] edge = Array.Empty<Drift>();
    private Drift[] gaps = Array.Empty<Drift>();
    private Rect[] protectedAreas = Array.Empty<Rect>();
    private Rect bounds = Rect.Empty;
    private Rect tray = Rect.Empty;
    private readonly record struct RidgePoint(Drift? Edge, double EdgeFactor, bool Protected, Drift? Gap, double GapFactor)
    {
        internal float Height()
        {
            double baseHeight = (Edge?.Height ?? 0) * EdgeFactor;
            return (float)Math.Max(Protected ? Math.Min(.8, baseHeight) : baseHeight, (Gap?.Height ?? 0) * GapFactor);
        }
    }
    private RidgePoint[] profile = Array.Empty<RidgePoint>();
    private float[] ridge = Array.Empty<float>();
    private bool dirty;
    private double nextRidgeUpdate;
    private double nextShed;
    private Drift? shedding;

    internal TaskbarSnow(Random random) => this.random = random;

    internal double HeightScale { get; set; } = 1;
    // At the top of the slider, exposed peaks reach the centre of the taskbar icons.
    private const double PeakPanelFraction = .55;
    private double CalibratedHeight(double baseline, double peak)
    {
        if (HeightScale <= 1) return baseline * HeightScale;
        double blend = Math.Clamp((HeightScale - 1) / .75, 0, 1);
        return baseline + (peak - baseline) * blend;
    }

    private double HeightLimit(Drift drift)
    {
        double baseline = drift.IsGap ? Math.Min(GapMaxHeight, (drift.Right - drift.Left) * .67)
            : Math.Min(EdgeMaxHeight, bounds.Height * .18);
        return CalibratedHeight(baseline, bounds.Height * PeakPanelFraction);
    }

    private double Threshold(Drift drift) => Math.Min(HeightLimit(drift),
        CalibratedHeight(drift.IsGap ? GapSheddingThreshold : EdgeSheddingThreshold,
            bounds.Height * PeakPanelFraction * .95));

    internal int GapCount => gaps.Length;
    internal int EdgeLandings { get; private set; }
    internal int GapLandings { get; private set; }
    internal int CompletedSheds { get; private set; }
    internal int GapSheds { get; private set; }
    internal int RegrownGaps { get; private set; }
    internal int Emitted { get; private set; }

    internal void SetLayout(Rect bar, Rect trayArea, Rect[] appFaces, Rect[] buttons)
    {
        if (bar != bounds)
        {
            bounds = bar;
            edge = Array.Empty<Drift>();
            gaps = Array.Empty<Drift>();
            shedding = null;
            Array.Clear(grains);
        }

        tray = trayArea;
        protectedAreas = buttons;
        var previousEdge = edge;
        var previousGaps = gaps;
        var sections = new List<Drift>();
        var openings = new List<Drift>();
        if (!bar.IsEmpty)
        {
            double right = tray.IsEmpty ? bar.Right : Math.Min(bar.Right, tray.Left - 2);
            for (double left = bar.Left; left < right; left += EdgeSectionWidth)
                sections.Add(KeepDrift(previousEdge, left, Math.Min(left + EdgeSectionWidth, right), false));

            if (bar.Width > bar.Height)
            {
                var sorted = appFaces.OrderBy(button => button.Left).ToArray();
                for (int i = 1; i < sorted.Length; i++)
                {
                    double left = sorted[i - 1].Right + 1;
                    double end = Math.Min(sorted[i].Left - 1, right);
                    if (end - left < MinimumGapWidth || end - left > 36) continue;
                    if (buttons.Any(button => button.Left < end && button.Right > left)) continue;
                    openings.Add(KeepDrift(previousGaps, left, end, true));
                }
            }
        }

        edge = sections.ToArray();
        gaps = openings.ToArray();
        if (shedding is not null && !Array.Exists(edge, drift => drift == shedding)
            && !Array.Exists(gaps, drift => drift == shedding))
            shedding = null;

        int samples = bar.IsEmpty ? 0 : Math.Clamp((int)Math.Ceiling(bar.Width), 2, 8192);
        if (ridge.Length != samples)
        {
            ridge = new float[samples];
            profile = new RidgePoint[samples];
        }
        for (int i = 0; i < samples; i++)
        {
            double x = bounds.Left + bounds.Width * (i + .5) / samples;
            int section = (int)((x - bounds.Left) / EdgeSectionWidth);
            if (section >= edge.Length || x >= edge[section].Right) { profile[i] = default; continue; }
            var gap = FindGap(x);
            profile[i] = new RidgePoint(edge[section], ShapeAt(x, edge[section]), IsProtected(x),
                gap, gap is null ? 0 : ShapeAt(x, gap));
        }
        dirty = true;
        nextRidgeUpdate = 0;
    }

    private static Drift KeepDrift(Drift[] previous, double left, double right, bool gap)
    {
        foreach (Drift drift in previous)
            if (drift.Left == left && drift.Right == right) return drift;
        return new Drift(left, right, gap);
    }

    internal bool TryLand(double x, double beforeY, double y, double radius, double now)
    {
        if (edge.Length == 0 || x < bounds.Left || x >= bounds.Right ||
            (!tray.IsEmpty && x >= tray.Left - 2)) return false;

        Drift? gap = FindGap(x);
        Drift drift = gap ?? edge[Math.Clamp((int)((x - bounds.Left) / EdgeSectionWidth), 0, edge.Length - 1)];
        double ySurface = bounds.Bottom - HeightAt(x, drift);
        if (beforeY + radius >= ySurface || y + radius < ySurface) return false;
        if (random.NextDouble() > (drift.IsGap ? .79 : .54)) return false;

        double previousHeight = drift.Height;
        double max = HeightLimit(drift);
        if (drift.Height >= max) return false;
        drift.Height = Math.Min(max, drift.Height + radius * (drift.IsGap ? .63 : .23));
        if (drift.Height == previousHeight) return false;
        dirty = true;
        if (drift.IsGap)
        {
            GapLandings++;
            if (drift.AwaitingRegrowth && drift.Remaining == 0)
            {
                RegrownGaps++;
                drift.AwaitingRegrowth = false;
            }
        }
        else EdgeLandings++;

        double threshold = Threshold(drift);
        if (drift.Height >= threshold && drift.Due == 0 && drift != shedding)
            drift.Due = now + MinimumSheddingDelay + random.NextDouble() * (MaximumSheddingDelay - MinimumSheddingDelay);
        return true;
    }

    private Drift? FindGap(double x)
    {
        foreach (var gap in gaps)
            if (x > gap.Left && x < gap.Right) return gap;
        return null;
    }

    private bool IsProtected(double x)
    {
        foreach (Rect button in protectedAreas)
            if (x >= button.Left - 1 && x <= button.Right + 1) return true;
        return false;
    }

    private double HeightAt(double x, Drift drift)
    {
        double height = drift.Height * ShapeAt(x, drift);
        return !drift.IsGap && IsProtected(x) ? Math.Min(height, .8) : height;
    }

    private static double ShapeAt(double x, Drift drift)
    {
        double t = Math.Clamp((x - drift.Left) / (drift.Right - drift.Left), 0, 1);
        if (drift.IsGap)
        {
            // Zero at both sides to leave the neighbouring button silhouettes clear.
            return Math.Pow(Math.Sin(t * Math.PI), 1.5) * (.92 + .08 * Math.Sin(t * 8 + drift.Left));
        }

        return Math.Min(1, Math.Min(t, 1 - t) * 8)
            * (.76 + .15 * Math.Sin(x * .27) + .09 * Math.Sin(x * .67 + 2));
    }

    internal void Sweep(Point previous, Point current, double elapsed, double now)
    {
        if (elapsed <= 0 || elapsed > .1 || !bounds.Contains(previous) || !bounds.Contains(current)) return;
        double dx = current.X - previous.X;
        double speed = Math.Abs(dx) / elapsed;
        if (speed < SweepSpeed || Math.Abs(dx) < Math.Abs(current.Y - previous.Y)) return;
        double left = Math.Min(current.X, previous.X) - 5;
        double right = Math.Max(current.X, previous.X) + 5;
        foreach (var drift in gaps) Blow(drift);
        foreach (var drift in edge) Blow(drift);

        void Blow(Drift drift)
        {
            if (drift == shedding || drift.Height < .95 || now < drift.NextSweep ||
                drift.Left >= right || drift.Right <= left) return;
            double x = (Math.Max(left, drift.Left) + Math.Min(right, drift.Right)) * .5;
            double y = bounds.Bottom - HeightAt(x, drift);
            double strength = Math.Clamp(speed / 1200, .32, 1);
            double removed = drift.Height * (.22 + .30 * strength);
            drift.Height -= removed;
            drift.NextSweep = now + .28;
            if (drift.Height <= .000001 || drift.Height < Threshold(drift)) drift.Due = 0;
            drift.AwaitingRegrowth = drift.IsGap;
            dirty = true;
            nextRidgeUpdate = now;
            int count = Math.Clamp((int)Math.Ceiling(removed * 3), 3, 12);
            for (int i = 0; i < count; i++)
            {
                grains[nextGrain] = new Grain(x, y,
                    Math.Sign(dx) * (34 + strength * 48 + random.NextDouble() * 36),
                    -12 - random.NextDouble() * 24, .6 + random.NextDouble(), .32 + random.NextDouble() * .26);
                nextGrain = (nextGrain + 1) % grains.Length;
                Emitted++;
            }
        }
    }

    internal void Advance(double now, double dt, double screenBottom)
    {
        if (shedding is null && now >= nextShed)
        {
            foreach (var drift in gaps) Consider(drift);
            foreach (var drift in edge) Consider(drift);

            if (shedding is { } next)
            {
                next.Direction = random.Next(2) == 0 ? -1 : 1;
                next.Due = 0;
                next.Remaining = HeightScale == 0 ? next.Height : Math.Min(next.Height * ShedFraction, Math.Max(0, next.Height - Math.Min(.32, Threshold(next) * .4)));
                next.ShedRate = next.Remaining / SheddingDuration;
                next.NextGrains = now + .16;
                next.Elapsed = -.16;
                Emit(next, screenBottom, 2, .18);
            }
        }

        if (shedding is { } active)
        {
            active.Elapsed += dt;
            if (active.Elapsed >= 0)
            {
                double amount = Math.Min(active.Remaining, dt * active.ShedRate);
                active.Height -= amount;
                active.Remaining -= amount;
                dirty = true;
                if (now >= active.NextGrains)
                {
                    active.NextGrains = now + .095;
                    Emit(active, screenBottom, random.Next(2, 5), .18 + .55 *
                        Math.Clamp(active.Elapsed / SheddingDuration, 0, 1));
                }
                if (active.Remaining <= 0)
                {
                    CompletedSheds++;
                    if (active.IsGap)
                    {
                        GapSheds++;
                        active.AwaitingRegrowth = true;
                    }
                    shedding = null;
                    nextShed = now + .9 + random.NextDouble() * 1.1;
                }
            }
        }

        for (int i = 0; i < grains.Length; i++)
        {
            ref Grain grain = ref grains[i];
            if (grain.Life <= 0) continue;
            grain.Life -= dt;
            grain.X += grain.Vx * dt;
            grain.Y += grain.Vy * dt;
            grain.Vy += 32 * dt;
            if (grain.Y - grain.Radius > screenBottom) grain.Life = 0;
        }

        void Consider(Drift drift)
        {
            if (drift.Height <= .000001 || drift.Height < Threshold(drift)) drift.Due = 0;
            else if (drift.Due == 0) drift.Due = now + MinimumSheddingDelay + random.NextDouble() * (MaximumSheddingDelay - MinimumSheddingDelay);
            if (drift.Due > 0 && drift.Due <= now && (shedding is null || drift.Due < shedding.Due))
                shedding = drift;
        }
    }

    private void Emit(Drift drift, double screenBottom, int count, double progress)
    {
        for (int i = 0; i < count; i++)
        {
            double t = Math.Clamp(progress + (random.NextDouble() - .5) * .16, .1, .9);
            double x = drift.Left + (drift.Right - drift.Left) * (drift.Direction > 0 ? 1 - t : t);
            double y = bounds.Bottom - HeightAt(x, drift);
            // On a flush taskbar the particles cross the screen edge. On a floating one they fade.
            double life = screenBottom - bounds.Bottom <= 3 ? .55 : .22 + random.NextDouble() * .26;
            grains[nextGrain] = new Grain(x, y, drift.Direction * (5 + random.NextDouble() * 12),
                9 + random.NextDouble() * 22, .6 + random.NextDouble() * 1.05, life);
            nextGrain = (nextGrain + 1) % grains.Length;
            Emitted++;
        }
    }

    internal void Draw(DirectRain gpu, float scale, double now)
    {
        if (!bounds.IsEmpty && ridge.Length > 0)
        {
            bool changed = dirty && now >= nextRidgeUpdate;
            if (changed)
            {
                for (int i = 0; i < ridge.Length; i++)
                    ridge[i] = profile[i].Height();
                dirty = false;
                nextRidgeUpdate = now + (shedding is null ? .16 : .05);
            }
            gpu.DrawSnowBank(ridge, (float)bounds.Bottom, (float)bounds.Left, (float)bounds.Width, scale, changed);
        }
        foreach (var grain in grains)
        {
            if (grain.Life <= 0 || (!tray.IsEmpty && tray.Contains(grain.X, grain.Y))) continue;
            float radius = (float)(grain.Radius * Math.Min(1, grain.Life * 6)) * scale;
            gpu.SnowDot((float)grain.X * scale, (float)grain.Y * scale, radius);
        }
    }

    internal double SnowHeightAt(double x)
    {
        int section = (int)((x - bounds.Left) / EdgeSectionWidth);
        if (section < 0 || section >= edge.Length || x >= edge[section].Right) return 0;
        double height = HeightAt(x, edge[section]);
        var gap = FindGap(x);
        return gap is null ? height : Math.Max(height, HeightAt(x, gap));
    }

    private sealed class Drift(double left, double right, bool gap)
    {
        internal double Left { get; } = left;
        internal double Right { get; } = right;
        internal bool IsGap { get; } = gap;
        internal double Height, Due, Remaining, ShedRate, NextGrains, Elapsed;
        internal bool AwaitingRegrowth;
        internal int Direction;
        internal double NextSweep;
    }

    private record struct Grain(double X, double Y, double Vx, double Vy, double Radius, double Life);
}
