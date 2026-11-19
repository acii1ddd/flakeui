using System;
using System.Collections.Generic;
using System.Windows;

internal sealed class NeonFrame
{
    private readonly List<Stroke> strokes = new();
    private readonly List<Rect> occluders = new();
    private double length;
    private float focus;
    private Point? cursor;
    private nint active;
    private nint landedOn;
    private double landingX, landingY, flash;
    internal bool HasLandingFlash => flash > 0 && active != 0 && landedOn == active && StrokeCount > 0;

    internal int StrokeCount => strokes.Count;

    internal void Land(nint handle, double x, double y)
    {
        landedOn = handle;
        landingX = x;
        landingY = y;
        flash = 1;
    }

    internal void Advance(RainSurface.WindowField windows, Size size, nint foreground, bool refreshed, double dt, bool trackCursor = true)
    {
        flash = Math.Max(0, flash - dt * 2.7);
        if (refreshed || foreground != active) Rebuild(windows.Platforms, size, foreground);
        if (trackCursor) UpdateCursor(windows);
    }

    internal void UpdateCursor(RainSurface.WindowField windows) =>
        cursor = Native.GetCursorPos(out var mouse) ? windows.ToLocal(mouse.X, mouse.Y) : null;

    internal void UpdateFocus(RainSurface.WindowField windows, bool activeWindow, double dt)
    {
        float target = activeWindow ? 1 : 0;
        focus = (float)(target + (focus - target) * Math.Exp(-dt * 9));
        if (focus < .005f) focus = 0;
        if (activeWindow) UpdateCursor(windows);
    }

    internal void Rebuild(IReadOnlyList<RainSurface.WindowField.Platform> platforms, Size size, nint foreground)
    {
        active = foreground;
        strokes.Clear();
        occluders.Clear();
        length = 0;
        foreach (var platform in platforms)
        {
            if (platform.Handle != foreground)
            {
                occluders.Add(platform.Bounds);
                continue;
            }

            Rect box = Rect.Intersect(platform.Bounds, new Rect(size));
            if (box.Width < 24 || box.Height < 24) return;
            box.Inflate(-3, -3);
            double radius = Math.Min(12, Math.Min(box.Width, box.Height) / 2);
            Point previous = new(box.Right - radius, box.Top);
            for (int i = 0; i < 4; i++)
            {
                double cx = i < 2 ? box.Right - radius : box.Left + radius;
                double cy = i == 0 || i == 3 ? box.Top + radius : box.Bottom - radius;
                for (int j = 0; j <= 5; j++)
                {
                    double angle = (i - 1 + j / 5.0) * Math.PI / 2;
                    var point = new Point(cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius);
                    AddVisible(previous, point);
                    previous = point;
                }
            }
            AddVisible(previous, new Point(box.Right - radius, box.Top));
            return;
        }
    }

    private void AddVisible(Point start, Point end)
    {
        Vector delta = end - start;
        double size = delta.Length;
        if (size < .1) return;
        double t = 0;
        while (t < 1)
        {
            double next = 1, covered = t;
            foreach (var box in occluders)
            {
                double enter = 0, exit = 1;
                if (!Clip(start.X, delta.X, box.Left - 8, box.Right + 8, ref enter, ref exit)
                    || !Clip(start.Y, delta.Y, box.Top - 8, box.Bottom + 8, ref enter, ref exit)
                    || exit <= t) continue;
                if (enter <= t) covered = Math.Max(covered, exit);
                else next = Math.Min(next, enter);
            }
            if (covered > t) t = covered;
            else
            {
                strokes.Add(new Stroke(start + delta * t, start + delta * next, length + t * size, (next - t) * size));
                t = next;
            }
        }
        length += size;
    }

    private static bool Clip(double origin, double delta, double min, double max, ref double enter, ref double exit)
    {
        if (Math.Abs(delta) < .001) return origin >= min && origin <= max;
        double a = (min - origin) / delta, b = (max - origin) / delta;
        enter = Math.Max(enter, Math.Min(a, b));
        exit = Math.Min(exit, Math.Max(a, b));
        return enter < exit;
    }

    internal void Draw(DirectRain gpu, float scale, double seconds, bool quiet = false, bool landingOnly = false)
    {
        if (length <= 0 || strokes.Count == 0 || (landingOnly && !HasLandingFlash)) return;
        float intensity = quiet ? focus : 1;
        if (intensity == 0)
        {
            foreach (var stroke in strokes)
                gpu.FrameStroke(stroke.Start, stroke.End, scale, 0, intensity);
            return;
        }
        double highlight = (seconds * 145) % length;
        double cursorRange = quiet ? 84 : 100;
        double cursorGap = cursorRange * cursorRange;
        double landingAt = 0, landingGap = double.PositiveInfinity;
        foreach (var stroke in strokes)
        {
            Vector delta = stroke.End - stroke.Start;
            if (!landingOnly && cursor is { } mouse)
            {
                double t = Math.Clamp(Vector.Multiply(mouse - stroke.Start, delta) / delta.LengthSquared, 0, 1);
                double gap = (mouse - (stroke.Start + delta * t)).LengthSquared;
                if (gap < cursorGap)
                {
                    cursorGap = gap;
                    highlight = stroke.Offset + stroke.Length * t;
                }
            }
            if (flash > 0 && landedOn == active && Math.Abs(delta.Y) < .1)
            {
                double t = Math.Clamp((landingX - stroke.Start.X) / delta.X, 0, 1);
                double gap = Math.Abs(landingY - stroke.Start.Y) + Math.Abs(landingX - (stroke.Start.X + delta.X * t));
                if (gap < landingGap)
                {
                    landingGap = gap;
                    landingAt = stroke.Offset + stroke.Length * t;
                }
            }
            if (!landingOnly) gpu.FrameStroke(stroke.Start, stroke.End, scale, 0, intensity);
        }

        foreach (var stroke in strokes)
        {
            if (quiet)
            {
                float hover = (float)Math.Clamp(1 - Math.Sqrt(cursorGap) / cursorRange, 0, 1);
                float soft = intensity * hover;
                if (soft > .01f)
                {
                    Accent(stroke, highlight, 58, .18f * soft);
                    Accent(stroke, highlight, 31, .40f * soft);
                    Accent(stroke, highlight, 13, .78f * soft);
                }
            }
            else if (!landingOnly) Accent(stroke, highlight, 48, .9f);
            if (landingGap < double.PositiveInfinity)
            {
                double spread = (1 - flash) * 95;
                Accent(stroke, landingAt - spread, 18 + 18 * flash, (float)flash);
                Accent(stroke, landingAt + spread, 18 + 18 * flash, (float)flash);
            }
        }

        void Accent(Stroke stroke, double at, double radius, float brightness)
        {
            for (int cycle = -1; cycle <= 1; cycle++)
            {
                double middle = at + cycle * length;
                double start = Math.Max(0, middle - radius - stroke.Offset);
                double end = Math.Min(stroke.Length, middle + radius - stroke.Offset);
                if (end <= start) continue;
                Vector unit = (stroke.End - stroke.Start) / stroke.Length;
                gpu.FrameStroke(stroke.Start + unit * start, stroke.Start + unit * end, scale, brightness);
            }
        }
    }

    private readonly record struct Stroke(Point Start, Point End, double Offset, double Length);
}
