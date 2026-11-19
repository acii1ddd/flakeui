using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

internal sealed class WindowStyleScene
{
    private readonly RainSurface.WindowField windows = new();
    private readonly Dictionary<nint, NeonFrame> frames = new();
    private readonly HashSet<nint> visible = new();
    private readonly List<nint> removed = new();
    private nint foreground;
    private long previous;

    internal int VisibleFrames { get; private set; }

    internal void Advance(long now, nint overlay, Matrix transform, Size viewport)
    {
        double dt = previous == 0 ? 1.0 / 60 : Math.Min(.05, System.Diagnostics.Stopwatch.GetElapsedTime(previous, now).TotalSeconds);
        previous = now;
        foreground = Native.GetForegroundWindow();
        if (windows.Refresh(now, overlay, transform, viewport))
        {
            visible.Clear();
            VisibleFrames = 0;
            foreach (var platform in windows.Platforms)
            {
                visible.Add(platform.Handle);
                if (!frames.TryGetValue(platform.Handle, out var frame))
                    frames.Add(platform.Handle, frame = new NeonFrame());
                frame.Rebuild(windows.Platforms, viewport, platform.Handle);
                if (frame.StrokeCount > 0) VisibleFrames++;
            }
            removed.Clear();
            foreach (var handle in frames.Keys)
                if (!visible.Contains(handle)) removed.Add(handle);
            foreach (var handle in removed) frames.Remove(handle);
        }

        foreach (var (handle, frame) in frames)
            frame.UpdateFocus(windows, handle == foreground, dt);
    }

    internal void Draw(DirectRain gpu, float scale, double seconds)
    {
        foreach (var (handle, frame) in frames)
            if (handle != foreground) frame.Draw(gpu, scale, seconds, quiet: true);
        if (frames.TryGetValue(foreground, out var active))
            active.Draw(gpu, scale, seconds, quiet: true);
    }
}
