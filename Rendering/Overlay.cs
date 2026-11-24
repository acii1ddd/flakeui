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

internal enum Mode { Off, Empty, Rain, Light }

internal sealed class Overlay : Window
{
    internal static readonly string MetricsPath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "neon-rain-metrics.txt"));

    private readonly RainSurface surface = new();
    private IntPtr hwnd;
    internal Mode CurrentMode { get; private set; }
    private System.Windows.Threading.DispatcherTimer? measurement;
    private int generation;
    internal bool HasRendered { get; private set; }

    internal Overlay()
    {
        Title = "flakeui";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = surface;
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            int styles = Native.GetWindowLong(hwnd, -20);
            Native.SetWindowLong(hwnd, -20, styles | 0x20 | 0x80 | 0x08000000);
        };
        Closed += (_, _) => CompositionTarget.Rendering -= Render;
    }

    internal void SetMode(Mode mode)
    {
        if (mode == CurrentMode) return;
        generation++;
        CompositionTarget.Rendering -= Render;
        CurrentMode = mode;
        surface.SetMode(mode);
        if (mode == Mode.Off)
        {
            Hide();
            return;
        }

        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        Width = 1;
        Height = 1;
        Show();
        Native.SetWindowPos(hwnd, new IntPtr(-1), bounds.Left, bounds.Top,
            bounds.Width, bounds.Height, 0x0010);
        if (mode is Mode.Rain or Mode.Light) CompositionTarget.Rendering += Render;
    }

    private void Render(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame || PresentationSource.FromVisual(this) is not HwndSource source
            || source.CompositionTarget is null) return;
        surface.Advance(frame.RenderingTime, hwnd, source.CompositionTarget.TransformFromDevice);
        HasRendered |= ActualWidth > 100 && ActualHeight > 100;
    }

    internal void Measure(Action<string> done, int seconds = 10)
    {
        if (measurement is not null) return;
        var mode = CurrentMode;
        int startGeneration = generation;
        var frames = surface.StartSample();
        using var current = Process.GetCurrentProcess();
        var initialCpu = current.TotalProcessorTime;
        var elapsed = Stopwatch.StartNew();
        measurement = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        measurement.Tick += (_, _) =>
        {
            measurement?.Stop();
            measurement = null;
            surface.StopSample();
            if (startGeneration != generation)
            {
                done("Mode changed. Run the measurement again.");
                return;
            }
            using var sampleProcess = Process.GetCurrentProcess();
            double cpuMs = (sampleProcess.TotalProcessorTime - initialCpu).TotalMilliseconds;
            string report = $"{mode}: {frames.Summarize()}, CPU={cpuMs:F0} ms over {elapsed.Elapsed.TotalSeconds:F1} s";
            try
            {
                File.AppendAllText(MetricsPath, $"{DateTime.Now:O} {report}{Environment.NewLine}");
                done(report);
            }
            catch (IOException) { done(report + " (could not save)"); }
            catch (UnauthorizedAccessException) { done(report + " (could not save)"); }
        };
        measurement.Start();
    }
}
