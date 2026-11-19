using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

internal sealed class MotionBenchmark : IDisposable
{
    private const int Capacity = 4096;
    private const int PhaseOffset = sizeof(int);
    private const int HeaderSize = 2 * sizeof(int);
    private readonly string name = "Local\\NeonRainMotion-" + Guid.NewGuid().ToString("N");
    private readonly MemoryMappedFile map;
    private readonly MemoryMappedViewAccessor view;
    private readonly EventWaitHandle stop;
    private readonly Process child;
    private readonly List<(string Name, long Begin, long End)> phases = new();

    internal MotionBenchmark()
    {
        map = MemoryMappedFile.CreateNew(name, HeaderSize + Capacity * sizeof(long));
        view = map.CreateViewAccessor();
        stop = new EventWaitHandle(false, EventResetMode.ManualReset, name + "-stop");
        try
        {
            var command = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(command.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                command.ArgumentList.Add(typeof(MotionBenchmark).Assembly.Location);
            command.ArgumentList.Add("--benchmark-motion-worker");
            command.ArgumentList.Add(name);
            child = Process.Start(command) ?? throw new InvalidOperationException("Could not start motion worker.");
        }
        catch
        {
            stop.Dispose();
            view.Dispose();
            map.Dispose();
            throw;
        }
    }

    internal bool IsReady => view.ReadInt32(0) >= 2 && !child.HasExited;
    internal bool HasExited => child.HasExited;
    internal void SetPhase(int phase) => view.Write(PhaseOffset, phase);
    internal void Record(string phase, long begin) => phases.Add((phase, begin, Stopwatch.GetTimestamp()));

    internal void WriteReport()
    {
        var count = Math.Clamp(view.ReadInt32(0), 0, Capacity);
        foreach (var phase in phases)
        {
            var intervals = new List<double>();
            long previous = 0;
            for (int i = 0; i < count; i++)
            {
                long stamp = view.ReadInt64(HeaderSize + i * sizeof(long));
                if (stamp < phase.Begin) continue;
                if (stamp > phase.End) break;
                if (previous != 0) intervals.Add(Stopwatch.GetElapsedTime(previous, stamp).TotalMilliseconds);
                previous = stamp;
            }
            string timing = "no movement samples";
            if (intervals.Count > 0)
            {
                int longIntervals = intervals.FindAll(interval => interval > 25).Count;
                intervals.Sort();
                double p95 = intervals[(int)Math.Ceiling(intervals.Count * .95) - 1];
                timing = $"intervals={intervals.Count}, p95={p95:F1} ms, max={intervals[^1]:F1} ms, >25 ms={longIntervals}";
            }
            File.AppendAllText(Overlay.MetricsPath, $"Window motion / {phase.Name}: {timing}{Environment.NewLine}");
        }
    }

    public void Dispose()
    {
        stop.Set();
        if (!child.WaitForExit(1500) && !child.HasExited)
        {
            child.Kill();
            child.WaitForExit(1500);
        }
        child.Dispose();
        stop.Dispose();
        view.Dispose();
        map.Dispose();
    }

    internal static int RunWorker(string name)
    {
        using var map = MemoryMappedFile.OpenExisting(name);
        using var view = map.CreateViewAccessor();
        using var stop = EventWaitHandle.OpenExisting(name + "-stop");
        var app = new Application();
        var label = new System.Windows.Controls.TextBlock
        {
            FontSize = 26,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var target = new Window
        {
            Title = "Neon Rain: independent test window",
            Content = label, Width = 480, Height = 280, Top = 240, Left = 100
        };
        target.Show();
        var clock = Stopwatch.StartNew();
        var shutdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        shutdown.Tick += (_, _) =>
        {
            if (stop.WaitOne(0) || clock.Elapsed.TotalSeconds > 45) app.Shutdown();
        };
        int count = 0, phase = -1;
        TimeSpan? previous = null;
        EventHandler frame = (_, args) =>
        {
            if (args is not RenderingEventArgs rendered || previous == rendered.RenderingTime) return;
            previous = rendered.RenderingTime;
            int nextPhase = view.ReadInt32(PhaseOffset);
            if (phase != nextPhase)
            {
                phase = nextPhase;
                label.Text = phase switch { 1 => "WPF rain", 2 => "D3D11 rain", 3 => "D3D11 snow + runner", _ => "Overlay off" };
            }
            long now = Stopwatch.GetTimestamp();
            target.Left = 100 + 80 * (1 + Math.Sin(clock.Elapsed.TotalSeconds * 2));
            if (count < Capacity)
            {
                view.Write(HeaderSize + count * sizeof(long), now);
                view.Write(0, ++count);
            }
        };
        CompositionTarget.Rendering += frame;
        shutdown.Start();
        try { return app.Run(); }
        finally
        {
            shutdown.Stop();
            CompositionTarget.Rendering -= frame;
        }
    }
}
