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

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--benchmark-motion-worker")
        {
            Environment.ExitCode = MotionBenchmark.RunWorker(args[1]);
            return;
        }

        if (args.Contains("--benchmark") || args.Contains("--benchmark-native"))
        {
            Environment.ExitCode = args.Contains("--benchmark-native") ? RunIsolatedBenchmark(args.Contains("--snow-runner") ? NativeEffect.Snow | NativeEffect.Runner : NativeEffect.Rain) : RunBenchmark();
            return;
        }

        if (args.Contains("--smoke-native") || args.Contains("--smoke-native-maximized"))
        {
            Environment.ExitCode = SmokeNative(args.Contains("--smoke-native-maximized"));
            return;
        }

        if (args.Contains("--smoke-desktop-icons"))
        {
            var read = System.Threading.Tasks.Task.Run(DesktopFlight.ReadIcons);
            if (!read.Wait(TimeSpan.FromSeconds(5))) { Environment.ExitCode = 9; return; }
            WriteMetric($"Desktop icon smoke: visible items={read.Result.Length}");
            Environment.ExitCode = read.Result.Length > 0 ? 0 : 8;
            return;
        }

        if (args.Contains("--smoke-style"))
        {
            Environment.ExitCode = SmokeStyle();
            return;
        }

        if (args.Contains("--smoke-snow") || args.Contains("--smoke-snowbank"))
        {
            Environment.ExitCode = args.Contains("--smoke-snowbank") ? SmokeSnowSpacing() : SmokeSnow();
            return;
        }

        if (args.Contains("--smoke-runner-timing"))
        {
            Environment.ExitCode = SmokeRunnerTiming();
            return;
        }

        if (args.Contains("--smoke-runner") || args.Contains("--smoke-layers"))
        {
            Environment.ExitCode = SmokeRunner(args.Contains("--smoke-layers"));
            return;
        }

        if (args.Contains("--smoke-test"))
        {
            var testApp = new Application();
            var overlay = new Overlay();
            overlay.SetMode(Mode.Rain);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) => { timer.Stop(); testApp.Shutdown(overlay.HasRendered ? 0 : 2); };
            timer.Start();
            Environment.ExitCode = testApp.Run();
            return;
        }

        bool checkHeader = args.Contains("--smoke-header");
        bool checkSettings = checkHeader || args.Contains("--smoke-settings");
        using var mutex = new System.Threading.Mutex(true, @"Local\NeonRainOverlay", out bool first);
        if (!first && !checkSettings) return;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var menu = new Forms.ContextMenuStrip();
        using var iconStream = typeof(Program).Assembly.GetManifestResourceStream("flakeui.ico")!;
        using var trayIcon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        using var tray = new Forms.NotifyIcon
        {
            Icon = trayIcon,
            Text = "flakeui",
            ContextMenuStrip = menu,
            Visible = true
        };

        string results = Path.Combine(AppContext.BaseDirectory, "settings-review");
        string? testFile = checkSettings ? Path.Combine(Path.GetTempPath(), $"flakeui-{Guid.NewGuid():N}.json") : null;
        using var settings = new SettingsStore(testFile);
        FlakeUI.SettingsWindow? panel = null;
        NativeRainSession? native = null;
        var off = new Forms.ToolStripMenuItem("Off");
        var borders = new Forms.ToolStripMenuItem("Start Tacky Borders");

        int effectVersion = 0;
        NativeEffect CurrentEffects() => native?.Effect ?? NativeEffect.None;
        var measure = new Forms.ToolStripMenuItem("Measure DirectX (10 seconds)");

        void UpdateMenu()
        {
            off.Checked = native is null;
            measure.Enabled = native is not null;
            panel?.SyncEffects(CurrentEffects());
        }

        void SelectEffects(NativeEffect effects)
        {
            if (CurrentEffects() == effects) { UpdateMenu(); return; }
            effectVersion++;
            if (effects == NativeEffect.None)
            {
                native?.Dispose();
                native = null;
            }
            else
            {

                try
                {
                    if (native is null) native = new NativeRainSession(ShowError, effects);
                    else native.SetEffects(effects);
                    native.Preferences = settings.Values;
                }
                catch (COMException error) { ShowError(error); }
            }
            UpdateMenu();
        }

        void ShowError(COMException error)
        {
            SelectEffects(NativeEffect.None);
            SaveNativeError(error);
            tray.ShowBalloonTip(4000, "flakeui",
                "DirectX stopped. Select an effect to retry.", Forms.ToolTipIcon.Warning);
        }

        off.Click += (_, _) => SelectEffects(NativeEffect.None);
        menu.Opening += (_, _) => borders.Text = IsTackyBordersRunning() ? "Stop Tacky Borders" : "Start Tacky Borders";
        borders.Click += async (_, _) =>
        {
            borders.Enabled = false;
            try
            {
                using var running = FindTackyBorders();
                if (running is null) StartTackyBorders(tray);
                else await StopTackyBorders(running);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                tray.ShowBalloonTip(4500, "Tacky Borders", error.Message, Forms.ToolTipIcon.Warning);
            }
            catch (InvalidOperationException)
            {
                tray.ShowBalloonTip(4500, "Tacky Borders", "Windows could not stop the process.", Forms.ToolTipIcon.Warning);
            }
            finally { borders.Enabled = true; }
        };
        measure.Click += (_, _) =>
        {
            if (native is not { } session) return;
            tray.ShowBalloonTip(2000, "flakeui", "Move a window for 10 seconds.", Forms.ToolTipIcon.Info);
            int measuredVersion = effectVersion;
            MeasureNative(session, 10,
                report => tray.ShowBalloonTip(3500, "flakeui", report, Forms.ToolTipIcon.Info),
                () => ReferenceEquals(native, session) && measuredVersion == effectVersion);
        };
        void OpenSettings()
        {
            if (panel is null)
            {
                panel = new FlakeUI.SettingsWindow(settings.Values, settings.Schedule,
                    effect => SelectEffects(effect == NativeEffect.Runner ? CurrentEffects() ^ effect : ToggleWeather(CurrentEffects(), effect)));
                panel.Closed += (_, _) => panel = null;
                panel.SyncEffects(CurrentEffects());
                panel.Show();
            }
            if (panel.WindowState == WindowState.Minimized) panel.WindowState = WindowState.Normal;
            panel.Activate();
        }
        menu.Items.Add("Настройки…", null, (_, _) => OpenSettings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(off);
        menu.Items.Add(borders);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(measure);
        menu.Items.Add("Open metrics", null, (_, _) =>
        {
            if (File.Exists(Overlay.MetricsPath))
                Process.Start(new ProcessStartInfo(Overlay.MetricsPath) { UseShellExecute = true });
        });
        menu.Items.Add("Exit", null, (_, _) => app.Shutdown());
        tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenSettings();
        };
        if (!checkSettings) StartTackyBorders(tray);
        SelectEffects(NativeEffect.Runner);
        if (checkSettings)
        {
            app.Dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    if (checkHeader)
                    {
                        SelectEffects(NativeEffect.Runner | NativeEffect.Snow);
                        OpenSettings();
                        await HeaderCheck.Run(panel!, native!);
                    }
                    else await SettingsCheck.Run(results, () => { menu.Items[0].PerformClick(); return panel!; },
                        () => SelectEffects(ToggleWeather(CurrentEffects(), NativeEffect.Rain)), () => SelectEffects(NativeEffect.Snow | NativeEffect.Runner),
                        () => native!, settings, testFile!);
                }
                catch (Exception error)
                {
                    Directory.CreateDirectory(results);
                    File.WriteAllText(Path.Combine(results,"error.txt"), error.ToString());
                    Environment.ExitCode = 8;
                }
                finally
                {
                    settings.Dispose();
                    File.Delete(testFile!);
                    app.Shutdown();
                }
            });
        }
        if (args.Contains("--settings")) OpenSettings();
        try { app.Run(); }
        finally
        {
            native?.Dispose();
            tray.Visible = false;
        }
    }

    private static NativeEffect ToggleWeather(NativeEffect effects, NativeEffect selected)
    {
        const NativeEffect weather = NativeEffect.Rain | NativeEffect.Snow;
        return (effects & ~weather) | ((effects & selected) == 0 ? selected : NativeEffect.None);
    }

    private static bool IsTackyBordersRunning()
    {
        using var running = FindTackyBorders();
        return running is not null;
    }

    private static Process? FindTackyBorders()
    {
        using var current = Process.GetCurrentProcess();
        Process? match = null;
        foreach (var process in Process.GetProcessesByName("tacky-borders"))
        {
            try
            {
                if (match is null && !process.HasExited && process.SessionId == current.SessionId)
                {
                    match = process;
                    continue;
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            process.Dispose();
        }
        return match;
    }

    private static async System.Threading.Tasks.Task StopTackyBorders(Process process)
    {
        try
        {
            process.Kill();
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 5)
        {
            // UAC approval is required for the instance previously started as administrator.
            var stop = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"))
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"/F /PID {process.Id} /FI \"IMAGENAME eq tacky-borders.exe\""
            };
            using var request = Process.Start(stop) ?? throw new InvalidOperationException();
            await request.WaitForExitAsync();
            if (request.ExitCode != 0 && !process.HasExited) throw new InvalidOperationException();
        }
        catch (InvalidOperationException) when (process.HasExited) { return; }

        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { throw new InvalidOperationException(); }
    }

    private static void StartTackyBorders(Forms.NotifyIcon tray)
    {
        try
        {
            if (IsTackyBordersRunning()) return;

            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var exe = Path.Combine(AppContext.BaseDirectory, "tacky-borders.exe");
            if (!File.Exists(exe)) exe = Path.Combine(downloads, "TackyBorders-preview", "tacky-borders.exe");
            if (!File.Exists(exe))
                throw new FileNotFoundException("The portable Tacky Borders executable is missing from Downloads.");
            using var started = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe)!
            });
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception)
        {
            tray.ShowBalloonTip(4500, "Tacky Borders", error.Message, Forms.ToolTipIcon.Warning);
        }
    }

    private static int RunBenchmark()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var target = new Window { Title = "Neon Rain benchmark", Width = 480, Height = 280, Top = 240, Left = 100 };
        target.Show();
        var overlay = new Overlay();
        var modes = new Queue<Mode>(new[] { Mode.Off, Mode.Empty, Mode.Rain, Mode.Light });
        var clock = Stopwatch.StartNew();
        var move = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        move.Tick += (_, _) => target.Left = 100 + 80 * (1 + Math.Sin(clock.Elapsed.TotalSeconds * 2));
        var warmup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            overlay.Measure(_ => Next(), 4);
        };
        void Next()
        {
            if (modes.TryDequeue(out var mode))
            {
                overlay.SetMode(mode);
                warmup.Start();
                return;
            }
            move.Stop();
            app.Shutdown(overlay.HasRendered ? 0 : 2);
        }
        move.Start();
        Next();
        return app.Run();
    }

    private static int RunIsolatedBenchmark(NativeEffect effects)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new Overlay();
        using var motion = new MotionBenchmark();
        var clock = Stopwatch.StartNew();
        var ready = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        var warmup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        int phase = 0;
        ready.Tick += (_, _) =>
        {
            if (motion.IsReady)
            {
                ready.Stop();
                warmup.Start();
            }
            else if (motion.HasExited || clock.Elapsed.TotalSeconds > 5)
            {
                ready.Stop();
                File.AppendAllText(Overlay.MetricsPath, "Benchmark: independent window did not start." + Environment.NewLine);
                app.Shutdown(5);
            }
        };
        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            long begin = Stopwatch.GetTimestamp();
            overlay.Measure(_ =>
            {
                motion.Record(phase == 0 ? "Off" : "WPF", begin);
                if (phase++ == 0)
                {
                    motion.SetPhase(1);
                    overlay.SetMode(Mode.Rain);
                    warmup.Start();
                }
                else
                {
                    motion.SetPhase(effects == NativeEffect.Rain ? 2 : 3);
                    overlay.SetMode(Mode.Off);
                    CompareNative(app, motion, effects);
                }
            }, 4);
        };
        ready.Start();
        int result = app.Run();
        motion.WriteReport();
        return result;
    }

    private static void CompareNative(Application app, MotionBenchmark motion, NativeEffect effects)
    {
        NativeRainSession? session = null;
        var warmup = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        void Fail(COMException error)
        {
            warmup.Stop();
            SaveNativeError(error);
            session?.Dispose();
            app.Shutdown(3);
        }

        try { session = new NativeRainSession(Fail, effects); }
        catch (COMException error) { Fail(error); return; }

        warmup.Tick += (_, _) =>
        {
            warmup.Stop();
            long begin = Stopwatch.GetTimestamp();
            MeasureNative(session, 4, _ =>
            {
                motion.Record($"D3D11 {effects}", begin);
                int result = session.Presented > 0 ? 0 : 4;
                session.Dispose();
                app.Shutdown(result);
            });
        };
        warmup.Start();
    }

    private static void MeasureNative(NativeRainSession session, int seconds, Action<string> done, Func<bool>? valid = null)
    {
        if (!session.TryStartSample(out var frames)) return;
        using var initial = Process.GetCurrentProcess();
        var initialCpu = initial.TotalProcessorTime;
        var stopwatch = Stopwatch.StartNew();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            session.StopSample();
            if (valid is not null && !valid())
            {
                done("Mode changed.");
                return;
            }
            using var proc = Process.GetCurrentProcess();
            var cpu = (proc.TotalProcessorTime - initialCpu).TotalMilliseconds;
            string report = $"D3D11 (DXGI paced, {session.Effect}): {frames.Summarize()}, CPU={cpu:F0} ms over "
                + $"{stopwatch.Elapsed.TotalSeconds:F1} s, accepted presents={session.Presented}, busy={session.Busy}";
            WriteMetric(report);
            done(report);
        };
        timer.Start();
    }

    private static void SaveNativeError(COMException error) => WriteMetric($"D3D11 error: {error}");

    private static void WriteMetric(string report)
    {
        try { File.AppendAllText(Overlay.MetricsPath, report + Environment.NewLine); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static int SmokeStyle()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        for (int i = 0; i < 2; i++)
        {
            var window = new Window { Title = "Neon style test", Width = 260, Height = 170 };
            window.Show();
            Native.SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1),
                bounds.Left + bounds.Width / 8 + i * (bounds.Width / 3), bounds.Top + bounds.Height / 4,
                260, 170, 0x50);
        }
        using var session = new NativeRainSession(_ => app.Shutdown(3), NativeEffect.Style);
        MeasureNative(session, 3, _ =>
        {
            WriteMetric($"Style smoke: visible frames={session.VisibleFrames}, presents={session.Presented}");
            app.Shutdown(session.VisibleFrames >= 2 && session.Presented > 30 ? 0 : 8);
        });
        return app.Run();
    }

    private static int SmokeSnowSpacing()
    {
        var snow = new TaskbarSnow(new Random(21));
        var bar = new Rect(10, 80, 210, 40);
        var tray = new Rect(170, 80, 50, 40);
        Rect[] faces = [new(40, 80, 28, 40), new(72, 80, 28, 40), new(114, 80, 28, 40)];
        snow.SetLayout(bar, tray, faces, faces);
        bool spacing = snow.GapCount == 1;
        for (int i = 0; i < 15; i++)
        {
            snow.TryLand(70, 95, 122, 2, .1);
            snow.TryLand(107, 95, 122, 2, .1);
            snow.TryLand(25, 95, 122, 2, .1);
        }
        bool landed = snow.GapLandings > 0 && snow.EdgeLandings > 0;
        int gapLandings = snow.GapLandings;
        for (int frame = 1; frame <= 720; frame++)
            snow.Advance(frame / 60.0, 1.0 / 60, 120);
        for (int i = 0; i < 12; i++) snow.TryLand(107, 95, 122, 2, 12.1);
        bool cycle = snow.GapSheds > 0 && snow.Emitted > 2 && snow.GapLandings > gapLandings && snow.RegrownGaps > 0;
        snow.SetLayout(bar, tray, faces[..2], faces[..2]);
        bool packed = snow.GapCount == 0 && !snow.TryLand(185, 95, 122, 2, 13);
        snow.SetLayout(new Rect(25, 90, 210, 40), Rect.Empty, [], []);
        bool moved = !snow.TryLand(15, 95, 132, 2, 14);
        WriteMetric($"Snow spacing: wide gap={spacing}, landings={landed}, cycle={cycle}, tight gap skipped={packed}, moved={moved}");
        bool sweeps = true;
        foreach (int direction in new[] { -1, 1 })
        {
            var wind = new TaskbarSnow(new Random(12));
            wind.SetLayout(bar, tray, faces, faces);
            for (int i = 0; i < 30; i++) wind.TryLand(107, 95, 122, 2, .1);
            var start = new Point(107 - direction * 5, 100);
            var end = new Point(107 + direction * 5, 100);
            wind.Sweep(start, end, .08, 1);
            bool quiet = wind.Emitted == 0;
            wind.Sweep(start, end, .016, 2);
            for (int i = 0; i < 20; i++) wind.TryLand(107, 95, 122, 2, 2.1);
            sweeps &= quiet && wind.Emitted >= 3 && wind.RegrownGaps == 1;
        }
        WriteMetric($"Snow mouse sweep: slow hover, both directions and regrowth={sweeps}");
        bool peaks = true;
        foreach (double height in new[] { 40.0, 56.0 })
        {
            var sample = new TaskbarSnow(new Random(21)) { HeightScale = 1.75 };
            sample.SetLayout(new Rect(10, 80, 210, height), tray, faces, faces);
            double floor = 80 + height;
            for (int i = 0; i < 100; i++) sample.TryLand(107, 80, floor, 2, .1);
            double peak = sample.SnowHeightAt(107);
            peaks &= peak >= height * .45 && peak <= height * .56 && sample.SnowHeightAt(90) <= 1;
            WriteMetric($"Maximum snow: panel={height} DIP, visible gap peak={peak:F1} DIP");
        }
        return spacing && landed && cycle && packed && moved && sweeps && peaks ? 0 : 8;
    }

    private static int SmokeSnow()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        nint bar = Native.FindWindow("Shell_TrayWnd", null);
        var point = new Native.POINT();
        if (Native.GetWindowRect(bar, out var rect))
        {
            point.X = rect.Left + (rect.Right - rect.Left) / 2;
            point.Y = rect.Top + (rect.Bottom - rect.Top) / 2;
        }
        nint hitBefore = Native.WindowFromPoint(point);
        using var session = new NativeRainSession(_ => app.Shutdown(3), NativeEffect.Rain);
        session.SetEffects(ToggleWeather(session.Effect, NativeEffect.Snow) | NativeEffect.Runner);
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        var watch = Stopwatch.StartNew();
        timer.Tick += (_, _) =>
        {
            var bank = session.SnowBanks!;
            bool clickedThrough = hitBefore != 0 && hitBefore == Native.WindowFromPoint(point);
            bool cycle = bank.GapSheds > 0 && bank.RegrownGaps > 0;
            if (!cycle && watch.Elapsed.TotalSeconds < 90) return;
            timer.Stop();
            WriteMetric($"Snow smoke ({watch.Elapsed.TotalSeconds:F0}s): gaps={bank.GapCount}, edge landings={bank.EdgeLandings}, gap landings={bank.GapLandings}, passed={session.SnowPassedTaskbar}, sheds={bank.CompletedSheds}, gap sheds={bank.GapSheds}, regrowth={bank.RegrownGaps}, grains={bank.Emitted}, presents={session.Presented}, click-through={clickedThrough}");
            bool passed = cycle && bank.EdgeLandings > 0 && bank.GapLandings > 0
                && session.SnowPassedTaskbar > 0 && bank.Emitted > 2 && clickedThrough
                && session.Effect == (NativeEffect.Snow | NativeEffect.Runner)
                && !session.HasVisibleRain && session.Presented > 30;
            app.Shutdown(passed ? 0 : 8);
        };
        timer.Start();
        return app.Run();
    }

    private static int SmokeRunnerTiming()
    {
        var fixture = new Window { Title = "flakeui · проверка прыжка", Left = 110, Top = 210,
            Width = 420, Height = 180, WindowStartupLocation = WindowStartupLocation.Manual, Topmost = true };
        fixture.Show();
        try
        {
            (double Flight, double Pause) Measure(int speed, int rest)
            {
                var scene = new RunnerScene { Preferences = new RainPreferences { JumpSpeed = speed, LandingRest = rest } };
                // Sample the desktop once so all three runs see a stationary route.
                long snapshot = Stopwatch.GetTimestamp();
                const double dt = .001;
                int airborne = 0, perched = 0;
                for (int tick = 0; tick < 10000; tick++)
                {
                    bool flying = scene.IsJumping;
                    scene.Advance(snapshot, dt, 0, Matrix.Identity,
                        new Size(SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
                    if (flying) airborne++;
                    else if (airborne > 0)
                    {
                        perched++;
                        if (scene.IsJumping) return (airborne * dt, perched * dt);
                    }
                }
                throw new InvalidOperationException("No complete jump on the test route");
            }
            var baseline = Measure(100, 100);
            var fast = Measure(150, 100);
            var patient = Measure(100, 150);
            WriteMetric($"Runner timing (flight/rest seconds): default={baseline}, fast={fast}, longer pause={patient}");
            return Math.Abs(fast.Flight * 1.5 - baseline.Flight) < .004 &&
                Math.Abs(fast.Pause - baseline.Pause) < .004 &&
                Math.Abs(patient.Flight - baseline.Flight) < .004 &&
                Math.Abs(patient.Pause - baseline.Pause * 1.5) < .004 ? 0 : 8;
        }
        finally { fixture.Close(); }
    }

    private static int SmokeRunner(bool layered)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        Window MakeWindow(int left, int top)
        {
            var window = new Window { Title = "Runner platform test", Width = 220, Height = 145 };
            window.Show();
            Native.SetWindowPos(new WindowInteropHelper(window).Handle, new nint(-1),
                bounds.Left + left, bounds.Top + top, 250, 145, 0x50);
            return window;
        }

        var first = MakeWindow(bounds.Width / 8, bounds.Height / 4);
        var moving = MakeWindow(bounds.Width / 2, bounds.Height / 3);
        moving.Activate();
        using var session = new NativeRainSession(_ => app.Shutdown(3), NativeEffect.Runner);
        if (layered) session.SetEffects(NativeEffect.Runner | NativeEffect.Rain);
        var cover = new Window { WindowStyle = WindowStyle.None, ShowActivated = false, Title = "Runner window switch test" };
        var clock = Stopwatch.StartNew();
        var move = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        move.Tick += (_, _) => Native.SetWindowPos(new WindowInteropHelper(moving).Handle, 0,
            bounds.Left + bounds.Width / 2 + (int)(40 * Math.Sin(clock.Elapsed.TotalSeconds * 3)),
            bounds.Top + bounds.Height / 3, 0, 0, 0x15);
        move.Start();
        var phase = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        int transfersBeforeRestore = 0;
        bool roofsCovered = false;
        phase.Tick += (_, _) =>
        {
            if (!cover.IsVisible)
            {
                cover.Show();
                Native.SetWindowPos(new WindowInteropHelper(cover).Handle, new nint(-1),
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x50);
                phase.Interval = TimeSpan.FromSeconds(2);
                return;
            }

            roofsCovered = session.RoofCount == 0;
            transfersBeforeRestore = session.Transfers;
            cover.Close();
            foreach (var platform in new[] { first, moving })
                Native.SetWindowPos(new WindowInteropHelper(platform).Handle, new nint(-1), 0, 0, 0, 0, 0x53);
            phase.Stop();
        };
        phase.Start();
        MeasureNative(session, 14, _ =>
        {
            move.Stop();
            phase.Stop();
            bool recovered = session.Transfers > transfersBeforeRestore;
            WriteMetric($"Runner smoke: roofs={session.RoofCount}, roofs covered={roofsCovered}, "
                + $"patrol distance={session.PatrolDistance:F0}, transfers resumed={recovered}, local hops={session.LocalHops}, transfers={session.Transfers}, frame strokes={session.FrameStrokes}, rain={session.HasVisibleRain}, presents={session.Presented}");
            app.Shutdown(roofsCovered && session.PatrolDistance > 20 && recovered && (!layered || session.HasVisibleRain) && session.FrameStrokes > 0 && session.Presented > 30 ? 0 : 8);
        });
        return app.Run();
    }

    private static int SmokeNative(bool maximized)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var target = new Window { Title = "Neon Rain input test", Background = new SolidColorBrush(Color.FromRgb(180, 66, 34)), Left = 80, Top = 100, Width = 320, Height = 180 };
        if (maximized) target.WindowStyle = WindowStyle.None;
        target.Show();
        var handle = new WindowInteropHelper(target).Handle;
        var bounds = Forms.Screen.PrimaryScreen!.Bounds;
        Native.SetWindowPos(handle, new nint(-1), bounds.Left + (maximized ? 0 : 80),
            bounds.Top + (maximized ? 0 : 100), maximized ? bounds.Width : 320, maximized ? bounds.Height : 180, 0x50);
        var point = new Native.POINT { X = maximized ? bounds.Width / 2 : 100, Y = maximized ? 180 : 80 };
        Native.ClientToScreen(handle, ref point);
        var before = Native.WindowFromPoint(point);
        NativeRainSession? session = null;
        try
        {
            session = new NativeRainSession(error =>
            {
                SaveNativeError(error);
                app.Shutdown(3);
            });
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            bool rainVisible = false;
            bool targetVisible = false;
            int samples = 0;
            var visibleAfter = Stopwatch.StartNew();
            timer.Tick += (_, _) =>
            {
                if (maximized && visibleAfter.Elapsed < TimeSpan.FromSeconds(10)) return;
                using var bitmap = new System.Drawing.Bitmap(240, 104);
                using var graphics = System.Drawing.Graphics.FromImage(bitmap);
                var output = graphics.GetHdc();
                var screen = Native.GetDC(0);
                bool copied;
                try
                {
                    copied = screen != 0 && Native.BitBlt(output, 0, 0, bitmap.Width, bitmap.Height,
                        screen, point.X - 80, point.Y - 48, 0x40CC0020);
                }
                finally
                {
                    if (screen != 0) Native.ReleaseDC(0, screen);
                    graphics.ReleaseHdc(output);
                }
                if (!copied)
                {
                    timer.Stop();
                    WriteMetric("Native smoke: could not sample pixels from the test window.");
                    app.Shutdown(9);
                    return;
                }
                int orange = 0, blue = 0;
                for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.R == 180 && pixel.G == 66 && pixel.B == 34) orange++;
                    if (pixel.B > 160 && pixel.B - pixel.R > 20) blue++;
                }
                targetVisible |= orange > bitmap.Width * bitmap.Height * .85;
                rainVisible |= blue > 8;
                if (++samples < 10) return;

                timer.Stop();
                var after = Native.WindowFromPoint(point);
                bool skipsOverlay = after == handle;
                WriteMetric($"Native smoke (maximized={maximized}): presents={session.Presented}, target visible to hit test before={before == handle}, "
                    + $"target reached during={skipsOverlay}, colored window visible through overlay={targetVisible}, rain visible={rainVisible}");
                app.Shutdown(session.Presented == 0 ? 4 : before != handle ? 7
                    : !skipsOverlay ? 6 : targetVisible && rainVisible ? 0 : 8);
            };
            timer.Start();
            return app.Run();
        }
        catch (COMException error)
        {
            SaveNativeError(error);
            app.Shutdown(3);
            return 3;
        }
        finally { session?.Dispose(); }
    }
}

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
            retireCredit += dt * 64;
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
            windows.Move(ref drop, dt, refreshed);
        }
        if (invalidate) InvalidateVisual();
        sample?.Work.Add(Stopwatch.GetElapsedTime(entered).TotalMilliseconds);
    }

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

internal sealed class FrameSample
{
    internal readonly List<double> Intervals = new(700);
    internal readonly List<double> Work = new(700);

    internal string Summarize()
    {
        if (Intervals.Count == 0) return "no animation callbacks";
        int over25 = Intervals.Count(interval => interval > 25);
        Intervals.Sort();
        Work.Sort();
        return $"intervals={Intervals.Count}, p50={Percentile(Intervals, .5):F1} ms, "
            + $"p95={Percentile(Intervals, .95):F1} ms, max={Intervals[^1]:F1} ms, >25 ms={over25}, "
            + $"simulation p95={Percentile(Work, .95):F2} ms";
    }

    private static double Percentile(List<double> sorted, double p) => sorted[(int)Math.Ceiling(sorted.Count * p) - 1];
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { internal int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] internal static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetDC(nint window);
    [DllImport("user32.dll")] internal static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32.dll")] internal static extern bool BitBlt(nint dest, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint operation);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] internal static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out POINT point);
    internal delegate bool WindowCallback(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] internal static extern bool EnumWindows(WindowCallback callback, IntPtr state);
    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] internal static extern int GetCloaked(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] internal static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr hwnd);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT rect, int size);
}
