using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlakeUI;

internal static class SettingsCheck
{
    internal static async Task Run(string directory, Func<SettingsWindow> open, Action toggleTrayRain,
        Action snowWithRunner, Func<NativeRainSession> current, SettingsStore store, string settingsPath)
    {
        Directory.CreateDirectory(directory);
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        var panel = open();
        Require(open() == panel, "Settings window duplicated");
        snowWithRunner();
        Require(panel.Rain.IsChecked == false, "Tray snow state");
        Click(panel.Rain);
        Require(current().Effect == (NativeEffect.Runner | NativeEffect.Rain) && panel.Rain.IsChecked == true, "Rain/snow switch");
        toggleTrayRain();
        Require(panel.Rain.IsChecked == false && current().Effect == NativeEffect.Runner, "Tray-to-panel sync");
        toggleTrayRain();
        panel.Speed.Value = 83.25;
        panel.Density.Value = 74.75;
        Click(panel.NightButton);
        await Task.Delay(650);
        using (var reload = new SettingsStore(settingsPath))
            Require(reload.Values.Night && reload.Values.Speed == 83 && reload.Values.Intensity == 75, "Persistence");
        Click(panel.ResetButton);
        Require(store.Values.Night && store.Values.Speed == 100 && store.Values.Intensity == 100 && panel.Rain.IsChecked == true, "Reset scope");
        Click(panel.DayButton);
        Click(panel.RunnerTab);
        Require(panel.Rain.IsChecked == true, "Runner must survive rain");
        panel.Speed.Value = 132;
        panel.Density.Value = 115;
        Click(panel.SnowTab);
        Require(panel.Rain.IsChecked == false, "Selecting a tab changed the weather");
        Click(panel.Rain);
        Require(current().Effect == (NativeEffect.Runner | NativeEffect.Snow), "Snow with runner");
        panel.Speed.Value = 144;
        panel.Density.Value = 138;
        await Task.Delay(700);
        using (var saved = new SettingsStore(settingsPath))
            Require(saved.Values.JumpSpeed == 132 && saved.Values.LandingRest == 115 &&
                saved.Values.SnowHeight == 144 && saved.Values.SnowIntensity == 138 &&
                saved.Values.Speed == 100, "Effect settings persistence");
        Click(panel.ResetButton);
        Require(store.Values.SnowHeight == 100 && store.Values.SnowIntensity == 100 &&
            store.Values.JumpSpeed == 132, "Snow reset scope");
        Click(panel.RunnerTab);
        Click(panel.ResetButton);
        Click(panel.Rain);
        Require(current().Effect == NativeEffect.Snow, "Independent runner toggle");
        Click(panel.Rain);
        Require(current().Effect == (NativeEffect.Runner | NativeEffect.Snow), "Runner restore");
        panel.Close();
        var reports = new List<string>();
        var session = current();
        for (int phase = 0; phase < 3; phase++)
        {
            if (phase == 1) { panel = open(); Click(panel.RunnerTab); }
            if (phase == 2) Click(panel.SnowTab);
            await Task.Delay(500);
            using var proc = Process.GetCurrentProcess();
            var cpu = proc.TotalProcessorTime;
            var span = Stopwatch.StartNew();
            Require(session.TryStartSample(out var frames), "Could not sample");
            File.WriteAllText(Path.Combine(directory,"phase.txt"),phase.ToString());
            var sweep = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(16) };
            double unsnapped = 100;
            void MoveThumb(object? sender, EventArgs e)
            {
                // Exercise the actual slider thumb drag event and its full WPF update path.
                var track = (Track)panel.Speed.Template.FindName("PART_Track", panel.Speed);
                double next = 100 + 45 * Math.Sin(span.Elapsed.TotalSeconds * 1.6);
                double pixels = (next - panel.Speed.Value) * (track.ActualWidth - track.Thumb.ActualWidth) / 100;
                unsnapped = next;
                track.Thumb.RaiseEvent(new DragDeltaEventArgs(pixels,0) { RoutedEvent = Thumb.DragDeltaEvent });
            }
            if (phase == 2) { sweep.Tick += MoveThumb; sweep.Start(); }
            await Task.Delay(30000);
            sweep.Stop();
            if (phase == 2) Require(Math.Abs(panel.Speed.Value-unsnapped) < 5, "Slider did not track");
            session.StopSample();
            double cpuPct = (proc.TotalProcessorTime-cpu).TotalSeconds/span.Elapsed.TotalSeconds*100;
            reports.Add($"{phase}: {frames.Summarize()}, CPU (one logical core)= {cpuPct:F2}%");
            File.WriteAllLines(Path.Combine(directory,"metrics.txt"), reports);
        }
        File.WriteAllText(Path.Combine(directory,"phase.txt"),"done");
        panel.Speed.Value=83; panel.Density.Value=74;
        panel.Topmost=true; panel.Activate();
        await Task.Delay(350);
        Click(panel.RunnerTab);
        await Task.Delay(450);
        Snapshot(panel,Path.Combine(directory,"runner-light.png"));
        Click(panel.SnowTab);
        await Task.Delay(900);
        Snapshot(panel,Path.Combine(directory,"snow-light.png"));
        Click(panel.NightButton);
        await Task.Delay(350);
        Snapshot(panel,Path.Combine(directory,"snow-night.png"));
        Click(panel.RunnerTab);
        await Task.Delay(450);
        Snapshot(panel,Path.Combine(directory,"runner-night.png"));
        var recording = new StringBuilder("<meta charset=utf-8><title>flakeui</title><body style='background:#252128;display:grid;place-items:center;height:95vh'><img alt='flakeui'><script>const frames=[");
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed.TotalSeconds<10)
        {
            double seconds=watch.Elapsed.TotalSeconds;
            if (seconds>4 && store.Values.Night) { Click(panel.DayButton); Click(panel.SnowTab); }
            panel.Speed.Value = 100+40*Math.Sin(seconds*1.9);
            var start=panel.PointToScreen(new Point(0,0));
            var end=panel.PointToScreen(new Point(panel.ActualWidth,panel.ActualHeight));
            using var bitmap=new System.Drawing.Bitmap((int)(end.X-start.X),(int)(end.Y-start.Y));
            using var graphics=System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)start.X,(int)start.Y,0,0,bitmap.Size);
            using var buffer=new MemoryStream();
            bitmap.Save(buffer,System.Drawing.Imaging.ImageFormat.Jpeg);
            recording.Append('[').Append((int)watch.ElapsedMilliseconds).Append(",\"").Append(Convert.ToBase64String(buffer.ToArray())).Append("\"],");
            await Task.Delay(60);
        }
        recording.Append("];let i=0;function play(){document.querySelector('img').src='data:image/jpeg;base64,'+frames[i][1];let n=(i+1)%frames.length;setTimeout(play,Math.max(50,frames[n][0]-frames[i][0]));i=n}play()</script>");
        File.WriteAllText(Path.Combine(directory,"interaction.html"),recording.ToString());
        panel.Close();
    }

    private static void Snapshot(Window panel, string path)
    {
        var dpi=VisualTreeHelper.GetDpi(panel);
        var image=new RenderTargetBitmap((int)Math.Round(panel.ActualWidth*dpi.DpiScaleX),
            (int)Math.Round(panel.ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        image.Render(panel);
        var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file=File.Create(path); encoder.Save(file);
    }
}