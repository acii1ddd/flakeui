using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

[Flags]
internal enum NativeEffect { None = 0, Rain = 1, Runner = 2, Style = 4, Snow = 8 }

internal sealed class NativeRainSession : IDisposable
{
    private readonly DirectRain gpu;
    private readonly RainSurface.WindowField windows = new();
    private readonly RainSurface.WindowField scanner = new();
    private System.Threading.Tasks.Task<bool>? scan;
    private long lastScan;
    private RainSurface? surface;
    private RunnerScene? runner;
    private SnowScene? snow;
    private readonly WindowStyleScene? style;
    private readonly Size viewport;
    private long previousFrame;
    private FrameSample? nativeSample;
    private readonly Matrix fromDevice;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Action<COMException> onError;
    private RegisteredWaitHandle? pending;
    private bool stopped;
    private bool sampling;

    internal RainPreferences? Preferences { get; set; }
    internal NativeEffect Effect { get; private set; }
    internal TaskbarSnow? SnowBanks => snow?.Banks;
    internal int SnowPassedTaskbar => snow?.PassedTaskbar ?? 0;
    internal bool HasVisibleRain => surface?.HasVisibleDrops ?? false;
    internal int VisibleFrames => style?.VisibleFrames ?? 0;
    internal int Transfers => runner?.Transfers ?? 0;
    internal int LocalHops => runner?.LocalHops ?? 0;
    internal int FrameStrokes => runner?.FrameStrokes ?? 0;
    internal int RoofCount => runner?.RoofCount ?? 0;
    internal double PatrolDistance => runner?.PatrolDistance ?? 0;
    internal int Presented => gpu.Presented;
    internal int Busy => gpu.Busy;
    internal nint Handle => gpu.Handle;

    internal System.Drawing.Rectangle Bounds { get; }
    internal uint Dpi { get; }
    internal NativeRainSession(Action<COMException> onError, NativeEffect effect = NativeEffect.Rain,
        System.Drawing.Rectangle? monitorBounds = null)
    {
        this.onError = onError;
        var bounds = Bounds = monitorBounds ?? Forms.Screen.PrimaryScreen!.Bounds;
        gpu = new DirectRain(bounds);
        try
        {
            Dpi = DirectRain.GetDpiForWindow(gpu.Handle);
            double inverseScale = 96.0 / Math.Max(96u, Dpi);
            fromDevice = Matrix.Identity;
            fromDevice.Scale(inverseScale, inverseScale);
            viewport = new Size(bounds.Width * fromDevice.M11, bounds.Height * fromDevice.M22);
            if (effect == NativeEffect.Style) style = new WindowStyleScene();
            SetEffects(effect);
            QueueFrame();
        }
        catch
        {
            gpu.Dispose();
            throw;
        }
    }

    internal void SetEffects(NativeEffect effects)
    {
        Effect = effects;
        if ((effects & NativeEffect.Rain) == 0) surface = null;
        else if (surface is null)
        {
            surface = new RainSurface(windows);
            surface.Measure(viewport);
            surface.Arrange(new Rect(viewport));
            surface.SetMode(Mode.Rain);
        }

        if ((effects & NativeEffect.Runner) == 0) runner = null;
        else runner ??= new RunnerScene(windows);

        if ((effects & NativeEffect.Snow) == 0) snow = null;
        else snow ??= new SnowScene();
    }

    private bool UpdateWindows(long now)
    {
        if (surface is null && runner is null) return false;
        bool refreshed = false;
        if (scan?.IsCompleted == true)
        {
            if (scan.GetAwaiter().GetResult())
            {
                windows.CopySnapshot(scanner);
                refreshed = true;
            }
            scan = null;
        }
        if (scan is null && (lastScan == 0 || Stopwatch.GetElapsedTime(lastScan, now).TotalMilliseconds >= 28))
        {
            lastScan = now;
            var handle = gpu.Handle;
            scan = System.Threading.Tasks.Task.Run(() => scanner.Refresh(Stopwatch.GetTimestamp(), handle, fromDevice, viewport));
        }
        return refreshed;
    }

    private void QueueFrame()
    {
        pending = ThreadPool.RegisterWaitForSingleObject(gpu.FrameReady, (_, timedOut) =>
            dispatcher.BeginInvoke(new Action(() => Render(timedOut)), DispatcherPriority.Render),
            null, 2000, executeOnlyOnce: true);
    }

    private void Render(bool timedOut)
    {
        if (stopped) return;
        pending?.Unregister(null);
        pending = null;
        try
        {
            if (timedOut) throw new COMException("DXGI did not signal frame readiness within two seconds.");
            float scale = (float)(1 / fromDevice.M11);
            long now = Stopwatch.GetTimestamp();
            double dt = previousFrame == 0 ? 1.0 / 60 : Stopwatch.GetElapsedTime(previousFrame, now).TotalSeconds;
            if (previousFrame != 0) nativeSample?.Intervals.Add(dt * 1000);
            previousFrame = now;
            bool refreshed = UpdateWindows(now);
            var elapsed = clock.Elapsed;
            if (surface != null) surface.Preferences = Preferences;
            surface?.Advance(elapsed, gpu.Handle, fromDevice, invalidate: false, refreshedSnapshot: refreshed);
            if (runner != null) runner.Preferences = Preferences;
            if (snow != null) snow.Preferences = Preferences;
            runner?.Advance(now, Math.Clamp(dt, 0, .05), gpu.Handle, fromDevice, viewport, refreshed);
            style?.Advance(now, gpu.Handle, fromDevice, viewport);
            snow?.Advance(elapsed.TotalSeconds, Math.Clamp(dt, 0, .05), gpu.Handle, fromDevice, viewport);
            nativeSample?.Work.Add(Stopwatch.GetElapsedTime(now).TotalMilliseconds);

            gpu.StartFrame();
            surface?.DrawNative(gpu, scale);
            snow?.Draw(gpu, scale, elapsed.TotalSeconds);
            runner?.Draw(gpu, scale, elapsed.TotalSeconds);
            style?.Draw(gpu, scale, elapsed.TotalSeconds);
            gpu.FinishFrame();
            QueueFrame();
        }
        catch (COMException error)
        {
            Dispose();
            onError(error);
        }
    }

    internal bool TryStartSample(out FrameSample frames)
    {
        if (sampling)
        {
            frames = null!;
            return false;
        }

        sampling = true;
        gpu.ResetCounters();
        frames = nativeSample = new FrameSample();
        return true;
    }

    internal void StopSample()
    {
        sampling = false;
        nativeSample = null;

    }

    public void Dispose()
    {
        if (stopped) return;
        stopped = true;
        pending?.Unregister(null);
        gpu.Dispose();
    }
}
