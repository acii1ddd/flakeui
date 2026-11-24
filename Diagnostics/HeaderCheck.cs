using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using FlakeUI;

internal static class HeaderCheck
{
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    internal static async Task Run(SettingsWindow panel, NativeRainSession session)
    {
        panel.SnowTab.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        panel.Topmost = true;
        panel.Activate();
        var handle = new WindowInteropHelper(panel).Handle;
        await Task.Delay(600);
        var report = new System.Text.StringBuilder();
        Native.GetCursorPos(out var original);
        try
        {
            foreach (int motion in new[] { 0, 2, 32 })
            {
                var hit = panel.PointToScreen(new Point(180, 24));
                Native.GetWindowRect(handle, out var before);
                SetCursorPos((int)hit.X, (int)hit.Y);
                await Task.Delay(120);
                if (Native.WindowFromPoint(new Native.POINT { X=(int)hit.X, Y=(int)hit.Y }) != handle)
                    throw new InvalidOperationException("Test header is covered");
                if (!session.TryStartSample(out var frames)) throw new InvalidOperationException("Sampling busy");
                await Task.Run(() =>
                {
                    for (int cycle = 0; cycle < 3; cycle++)
                    {
                        // Return to a point inside this window's header before every double click.
                        Native.GetWindowRect(handle, out var current);
                        int x = (int)hit.X + current.Left - before.Left;
                        int y = (int)hit.Y + current.Top - before.Top;
                        SetCursorPos(x, y);
                        if (Native.WindowFromPoint(new Native.POINT { X=x, Y=y }) != handle)
                            throw new InvalidOperationException("Header lost focus");
                        try
                        {
                            mouse_event(2,0,0,0,0); Thread.Sleep(65); mouse_event(4,0,0,0,0);
                            Thread.Sleep(85);
                            mouse_event(2,0,0,0,0);
                            int direction = cycle == 1 ? -1 : 1;
                            for (int step = 1; step <= 12; step++)
                            {
                                Thread.Sleep(16);
                                if (motion > 0) SetCursorPos(x + direction * motion * step / 12, y);
                            }
                            Thread.Sleep(75);
                        }
                        finally { mouse_event(4,0,0,0,0); }
                        Thread.Sleep(550);
                    }
                });
                session.StopSample();
                Native.GetWindowRect(handle, out var after);
                report.AppendLine($"Header double-click, input={motion}px, final displacement={after.Left-before.Left}px: {frames.Summarize()}");
            }
            File.AppendAllText(Overlay.MetricsPath, report.ToString());
        }
        finally { SetCursorPos(original.X, original.Y); }
    }
}
