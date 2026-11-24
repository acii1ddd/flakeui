using System;
using System.Windows;

internal sealed class PetalSwarm
{
    internal const int Capacity = 16;
    // A cherry petal, with a shallow notch at its broad end. Both renderers use this contour.
    internal static readonly Point[] Outline = [new(-5,.6),new(-4,-1),new(-2,-2.6),new(.4,-3.3),
        new(2.4,-3.1),new(4,-2.3),new(5,-1.1),new(3.5,-.15),new(5.1,.5),new(4,1.9),
        new(2,2.7),new(-.2,2.8),new(-2.6,2),new(-4,1.4)];
    internal struct Petal
    {
        internal Point Position;
        internal Vector Velocity;
        internal double Alpha, Angle, Width, Visibility;
    }
    internal readonly Petal[] Petals = new Petal[Capacity];
    private Point previous;
    private Vector wind, heading = new(1,0);
    private double time, quiet, twist;
    private bool started;

    internal void Step(Point cursor, double dt, double scale, int length)
    {
        dt = Math.Clamp(dt, .0001, .05);
        if (!started || (cursor - previous).Length > 1000 * scale)
        {
            started = true;
            previous = cursor;
            wind = default;
            for (int i = 0; i < Capacity; i++)
                Petals[i] = new Petal { Position = cursor + scale * Orbit(i) };
        }
        Vector delta = cursor - previous;
        previous = cursor;
        quiet = delta.Length > .4 * scale ? 0 : quiet + dt;
        wind += (delta / dt - wind) * (1 - Math.Exp(-12 * dt));
        double speed = wind.Length / scale;
        if (speed > 8) heading = wind / wind.Length;
        double trail = Math.Min(92, speed * .11) * length / 100;
        int count = quiet > .48 ? 3 : Math.Clamp(4 + (int)(speed / 110), 4, Capacity);
        time += dt;
        twist *= Math.Exp(-3.7 * dt);
        for (int i = 0; i < Capacity; i++)
        {
            ref var p = ref Petals[i];
            double fraction = (i + 1.0) / count;
            Vector offset = Orbit(i) - heading * (trail * Math.Min(1, fraction));
            if (twist > .01)
            {
                offset -= heading * (12 * twist);
                double a = twist * 2.1;
                offset = new Vector(offset.X * Math.Cos(a) - offset.Y * Math.Sin(a),
                    offset.X * Math.Sin(a) + offset.Y * Math.Cos(a));
            }
            Point target = cursor + offset * scale;
            double k = 100 + (i % 5) * 13;
            p.Velocity += (target - p.Position) * (k * dt);
            p.Velocity *= Math.Exp(-Math.Sqrt(k) * 1.55 * dt);
            p.Position += p.Velocity * dt;
            double clearance = Math.Clamp(((p.Position - cursor).Length / scale - 7) / 7, 0, 1);
            p.Alpha += ((i < count ? .86 : 0) - p.Alpha) * (1 - Math.Exp(-6 * dt));
            p.Angle = .6 * Math.Sin(time * 1.7 + i) + time * (.32 + .05 * (i % 4)) + i * 2.4;
            p.Width = .16 + .84 * Math.Abs(Math.Cos(time * (1 + .07 * i) + i));
            p.Visibility = clearance;
        }
    }

    private Vector Orbit(int i)
    {
        double angle = i * 2.39996 + time * (.55 + .09 * (i % 4)) + .4 * Math.Sin(time + i);
        double radius = 23 + 6 * Math.Sin(time * .7 + i * 1.3);
        return new Vector(radius * Math.Cos(angle), radius * Math.Sin(angle));
    }

    internal void Click(Point cursor, bool right, int strength, double scale)
    {
        if (!started || strength == 0) return;
        double gain = strength / 100.0;
        if (right) twist = Math.Min(2, twist + gain);
        for (int i = 0; i < Capacity; i++)
        {
            ref var p = ref Petals[i];
            Vector radial = p.Position - cursor;
            double distance = radial.Length;
            if (distance < .01 || distance > 160 * scale) continue;
            radial /= distance;
            Vector impulse = right ? new Vector(-radial.Y, radial.X) - heading * .35 : radial;
            p.Velocity += impulse * (scale * gain * 270 / (1 + distance / (70 * scale)));
            if (p.Velocity.Length > 650 * scale) p.Velocity *= 650 * scale / p.Velocity.Length;
        }
    }
}