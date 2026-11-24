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
        if(args.Contains("--smoke-desktop"))
        {
            Environment.ExitCode=DesktopCheck.Run();
            return;
        }
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
        bool checkCursor = args.Contains("--smoke-cursor");
        bool checkSettings = checkCursor || checkHeader || args.Contains("--smoke-settings");
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
        int effectVersion = 0;
        var displays = new Dictionary<string, NativeRainSession>(StringComparer.OrdinalIgnoreCase);
        NativeRainSession? native = null;
        using var switcher = new DesktopSwitcher(settings.Values);
        using var pond = new PondController(settings.Values, name => displays.GetValueOrDefault(name));
        void ApplyDesktop()
        {
            if (!switcher.Enable(settings.Values.DesktopSwitcherEnabled))
            {
                settings.Values.DesktopSwitcherEnabled = false;
                tray.ShowBalloonTip(4000,"flakeui","Windows не удалось подключить переключатель окон.",Forms.ToolTipIcon.Warning);
            }
            pond.Configure();
            settings.Schedule();
        }
        if (settings.Values.DesktopSwitcherEnabled) ApplyDesktop();
        CursorEffect? cursor = null;
        void StopDisplays()
        {
            foreach (var display in displays.Values) display.Dispose();
            displays.Clear();
            cursor?.Dispose();
            cursor = null;
            native = null;
        }
        void SyncDisplays(NativeEffect effects)
        {
            if (effects.HasFlag(NativeEffect.Cursor)) cursor ??= new CursorEffect(settings.Values);
            else if (cursor != null) { cursor.Dispose(); cursor = null; }
            var screens = Forms.Screen.AllScreens;
            foreach (var name in displays.Keys.ToArray())
            {
                var screen = Array.Find(screens, item => item.DeviceName == name);
                var display = displays[name];
                if (screen != null && screen.Bounds == display.Bounds && display.Dpi == DirectRain.GetDpiForWindow(display.Handle)) continue;
                display.Dispose();
                displays.Remove(name);
                effectVersion++;
            }
            foreach (var screen in screens)
            {
                if (!displays.TryGetValue(screen.DeviceName, out var display))
                {
                    display = new NativeRainSession(ShowError, effects, screen.Bounds);
                    displays.Add(screen.DeviceName, display);
                }
                else if (display.Effect != effects) display.SetEffects(effects);
                display.Cursor = cursor;
                display.Preferences = settings.Values;
                if (screen.Primary) native = display;
            }
            native = displays.Values.FirstOrDefault(d => ReferenceEquals(d, native)) ?? displays.Values.FirstOrDefault();
        }
        var off = new Forms.ToolStripMenuItem("Off");
        var borders = new Forms.ToolStripMenuItem("Start Tacky Borders");

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
                StopDisplays();
            }
            else
            {

                try
                {
                    SyncDisplays(effects);
                }
                catch (COMException error) { ShowError(error); }
            }
            UpdateMenu();
        }

        void ShowError(COMException error)
        {
            StopDisplays();
            UpdateMenu();
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
                    effect => SelectEffects(effect is NativeEffect.Runner or NativeEffect.Cursor ? CurrentEffects() ^ effect : ToggleWeather(CurrentEffects(), effect)));
                panel.DesktopChanged = ApplyDesktop;
                panel.PreviewPond = pond.Preview;
                panel.CancelPond = () => { if (!pond.Previewing) return false; pond.CancelPreview(); return true; };
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
                    if (checkCursor)
                    {
                        OpenSettings();
                        await CursorCheck.Run(panel!, SelectEffects, () => native, settings, testFile!, results);
                    }
                    else if (checkHeader)
                    {
                        SelectEffects(NativeEffect.Runner | NativeEffect.Snow);
                        OpenSettings();
                        SelectEffects(CurrentEffects() | NativeEffect.Cursor);
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
        using var displayTimer = new Forms.Timer { Interval = 2000 };
        displayTimer.Tick += (_, _) =>
        {
            if (native is null) return;
            try { SyncDisplays(native.Effect); }
            catch (COMException error) { ShowError(error); }
        };
        displayTimer.Start();
        if (args.Contains("--settings")) OpenSettings();
        try { app.Run(); }
        finally
        {
            displayTimer.Stop();
            StopDisplays();
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
