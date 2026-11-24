using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;

internal sealed class SnowScene
{
    private readonly Random random = new();
    private readonly Flake[] flakes = new Flake[270];
    private readonly double gustPhase = Random.Shared.NextDouble() * Math.Tau;
    private readonly TaskbarSnow banks;
    internal RainPreferences? Preferences { get; set; }
    private int activeCount;
    private Rect taskbar = Rect.Empty;
    private Rect tray = Rect.Empty;
    private Rect[] buttonFaces = Array.Empty<Rect>();
    private Rect[] obstacles = Array.Empty<Rect>();
    private Task<ControlBounds[]>? pendingControls;
    private nint taskbarHandle;
    private nint scannedHandle;
    private Rect scannedTaskbar;
    private double nextTaskbarCheck;
    private double nextButtonScan;

    private Point? previousMouse;
    private double previousMouseTime;
    private Native.POINT overlayOrigin;

    internal SnowScene() => banks = new TaskbarSnow(random);
    internal TaskbarSnow Banks => banks;
    internal int PassedTaskbar { get; private set; }

    internal void Advance(double now, double dt, nint overlay, Matrix fromDevice, Size size)
    {
        if (now >= nextTaskbarCheck)
        {
            nextTaskbarCheck = now + .35;
            RefreshTaskbar(now, overlay, fromDevice, size);
        }
        banks.HeightScale = Preferences?.SnowHeightScale ?? 1;
        int targetCount = (int)Math.Round(180 * (Preferences?.SnowIntensity ?? 100) / 100.0);
        if (activeCount < targetCount)
        {
            double startFloor = taskbar.IsEmpty ? size.Height : taskbar.Top;
            for (int i = activeCount; i < targetCount; i++) flakes[i] = Spawn(size.Width, now, activeCount == 0 ? startFloor : 0);
        }

        activeCount = targetCount;
        for (int i = 0; i < activeCount; i++)
        {
            ref var flake = ref flakes[i];
            double beforeY = flake.Y;
            flake.X += Math.Sin(now * flake.Drift + flake.Phase) * 22 * dt;
            if (flake.X < -4) flake.X += size.Width + 8;
            if (flake.X > size.Width + 4) flake.X -= size.Width + 8;

            flake.Y += flake.Speed * dt;
            if (!taskbar.IsEmpty && flake.Y >= taskbar.Top && beforeY < taskbar.Bottom)
            {
                if (flake.Steer) SteerAroundButtons(ref flake, beforeY, dt);
                if (banks.TryLand(flake.X, beforeY, flake.Y, flake.Radius, now))
                {
                    flake = Spawn(size.Width, now);
                    continue;
                }
            }

            if (flake.Y <= size.Height + flake.Radius) continue;
            PassedTaskbar++;
            flake = Spawn(size.Width, now);
        }
        if (!taskbar.IsEmpty && Native.GetCursorPos(out var mouse))
        {
            var point = fromDevice.Transform(new Point(mouse.X - overlayOrigin.X, mouse.Y - overlayOrigin.Y));
            if (previousMouse is Point previous)
                banks.Sweep(previous, point, now - previousMouseTime, now);
            previousMouse = point;
            previousMouseTime = now;
        }
        else previousMouse = null;
        banks.Advance(now, dt, size.Height);
    }

    private Flake Spawn(double width, double now, double initialFloor = 0)
    {
        double x = random.NextDouble();
        if (random.NextDouble() < .52)
        {
            double gust = .5 + .31 * Math.Sin(now * .075 + gustPhase);
            double scatter = (random.NextDouble() + random.NextDouble() - 1) * .19;
            x = Math.Clamp(gust + scatter, .01, .99);
        }

        return new Flake(x * width,
            initialFloor > 0 ? random.NextDouble() * initialFloor : -10 - random.NextDouble() * 90,
            25 + random.NextDouble() * 45,
            1 + random.NextDouble() * 1.8,
            .6 + random.NextDouble() * 1.5,
            random.NextDouble() * Math.Tau,
            random.NextDouble() < .78);
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    private void RefreshTaskbar(double now, nint overlay, Matrix fromDevice, Size size)
    {
        var monitor = MonitorFromWindow(overlay, 2);
        var bar = Native.FindWindow("Shell_TrayWnd", null);
        if (bar == 0 || MonitorFromWindow(bar, 0) != monitor)
        {
            bar = 0;
            while ((bar = FindWindowEx(0, bar, "Shell_SecondaryTrayWnd", null)) != 0)
                if (MonitorFromWindow(bar, 0) == monitor) break;
        }
        var origin = new Native.POINT();
        Native.ClientToScreen(overlay, ref origin);
        overlayOrigin = origin;

        Rect ToLocal(Rect screen)
        {
            screen.Offset(-origin.X, -origin.Y);
            screen.Transform(fromDevice);
            return screen;
        }

        Rect visible = Rect.Empty;
        if (bar != 0 && Native.IsWindowVisible(bar) && Native.GetWindowRect(bar, out var rect))
        {
            visible = ToLocal(new Rect(new Point(rect.Left, rect.Top), new Point(rect.Right, rect.Bottom)));
            visible.Intersect(new Rect(size));
            // Auto-hide can leave a thin strip visible while the panel is retracted.
            if (visible.IsEmpty || Math.Min(visible.Width, visible.Height) < 12)
                visible = Rect.Empty;
        }

        if (visible != taskbar || bar != taskbarHandle)
        {
            taskbar = visible;
            taskbarHandle = bar;
            tray = Rect.Empty;
            buttonFaces = Array.Empty<Rect>();
            obstacles = Array.Empty<Rect>();
            // Wait for control bounds before placing snow on a new panel layout.
            banks.SetLayout(Rect.Empty, Rect.Empty, buttonFaces, obstacles);
            nextButtonScan = now;
        }

        if (pendingControls?.IsCompleted == true)
        {
            if (pendingControls.IsCompletedSuccessfully && !taskbar.IsEmpty &&
                scannedHandle == bar && scannedTaskbar == taskbar)
            {
                var faces = new List<Rect>();
                var buttons = new List<Rect>();
                Rect trayArea = Rect.Empty;
                bool hasTrayButtons = false;
                foreach (var control in pendingControls.Result)
                {
                    Rect button = ToLocal(control.Bounds);
                    button.Intersect(taskbar);
                    if (button.IsEmpty || button.Width < 1 || button.Height < 1) continue;
                    if (control.Kind == ControlKind.Tray)
                    {
                        trayArea.Union(button);
                        continue;
                    }

                    if (control.Kind is ControlKind.App or ControlKind.TrayButton)
                    {
                        // UI Automation reports the hit target, which may touch its neighbour.
                        // Reserve the inner icon face and indicator, including a safety margin.
                        bool trayButton = control.Kind == ControlKind.TrayButton;
                        hasTrayButtons |= trayButton;
                        double inset = trayButton ? Math.Min(5, button.Width * .17) : button.Width <= 52 ? Math.Clamp((button.Width - 32) / 2, 0, 6) : 0;
                        button = new Rect(button.Left + inset, button.Top, button.Width - 2 * inset, button.Height);
                        faces.Add(button);
                    }
                    buttons.Add(button);
                }
                if (hasTrayButtons) trayArea = Rect.Empty;
                if (buttons.Count > 0 || !trayArea.IsEmpty)
                {
                    var sortedFaces = faces.OrderBy(x => x.Left).ToArray();
                    var sortedButtons = buttons.OrderBy(x => x.Left).ThenBy(x => x.Top).ToArray();
                    if (trayArea != tray || !sortedFaces.SequenceEqual(buttonFaces) ||
                        !sortedButtons.SequenceEqual(obstacles))
                    {
                        tray = trayArea;
                        buttonFaces = sortedFaces;
                        obstacles = sortedButtons;
                        banks.SetLayout(taskbar, tray, buttonFaces, obstacles);
                    }
                }
            }
            if (pendingControls.IsFaulted) _ = pendingControls.Exception;
            pendingControls = null;
        }

        if (taskbar.IsEmpty || pendingControls is not null || now < nextButtonScan) return;
        // Existing icon bounds suffice while dragging. UI Automation can involve synchronous
        // work in Explorer; defer the periodic rescan until the mouse button is released.
        if (obstacles.Length > 0 && System.Windows.Forms.Control.MouseButtons != System.Windows.Forms.MouseButtons.None)
            return;
        scannedHandle = bar;
        scannedTaskbar = taskbar;
        nextButtonScan = now + 1.5;
        pendingControls = Task.Run(() => ReadTaskbarControls(bar));
    }

    private static ControlBounds[] ReadTaskbarControls(nint bar)
    {
        try
        {
            var root = AutomationElement.FromHandle(bar);
            var filter = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "Taskbar.TaskListButtonAutomationPeer"),
                new PropertyCondition(AutomationElement.ClassNameProperty, "TrayNotifyWnd"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SearchButton"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            var bounds = new List<ControlBounds>();
            foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, filter))
            {
                try
                {
                    var current = element.Current;
                    if (current.IsOffscreen) continue;
                    Rect rectangle = current.BoundingRectangle;
                    if (rectangle.IsEmpty || rectangle.Width <= 0 || rectangle.Height <= 0) continue;
                    var kind = current.ClassName switch
                    {
                        "Taskbar.TaskListButtonAutomationPeer" => ControlKind.App,
                        "TrayNotifyWnd" => ControlKind.Tray,
                        _ => current.AutomationId switch
                        {
                            "StartButton" or "SearchButton" => ControlKind.App,
                            "SystemTrayIcon" => ControlKind.TrayButton,
                            _ => ControlKind.Other
                        }
                    };
                    bounds.Add(new ControlBounds(rectangle, kind));
                }
                catch (ElementNotAvailableException) { }
            }
            return bounds.ToArray();
        }
        catch (Exception error) when (error is COMException or ElementNotAvailableException
            or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return Array.Empty<ControlBounds>();
        }
    }

    private void SteerAroundButtons(ref Flake flake, double beforeY, double dt)
    {
        double radius = flake.Radius;
        double glyphTop = taskbar.Top + taskbar.Height * .24;
        double glyphBottom = taskbar.Top + taskbar.Height * .77;
        if (beforeY >= glyphBottom + radius || flake.Y + radius < glyphTop - 12) return;

        foreach (Rect button in obstacles)
        {
            double left = button.Left - radius;
            double right = button.Right + radius;
            if (flake.X <= left || flake.X >= right) continue;
            double target = flake.X - left < right - flake.X ? left : right;
            flake.X += Math.CopySign(Math.Min(Math.Abs(target - flake.X), 95 * dt), target - flake.X);
            if (flake.X > left && flake.X < right && beforeY <= glyphTop - radius)
                flake.Y = Math.Min(flake.Y, glyphTop - radius);
            break;
        }
    }

    internal void Draw(DirectRain gpu, float scale, double now)
    {
        foreach (var flake in flakes.AsSpan(0, activeCount))
        {
            if (flake.Y < 0) continue;
            double radius = flake.Radius;
            if (!taskbar.IsEmpty && flake.Y >= taskbar.Top)
            {
                if (tray.Contains(flake.X, flake.Y) || CoversGlyph(flake.X, flake.Y, radius)) continue;
                radius *= Math.Clamp((taskbar.Bottom - flake.Y) / 8, .45, 1);
            }
            gpu.SnowDot((float)flake.X * scale, (float)flake.Y * scale, (float)radius * scale);
        }
        banks.Draw(gpu, scale, now);
    }

    private bool CoversGlyph(double x, double y, double radius)
    {
        if (y + radius < taskbar.Top + taskbar.Height * .24 ||
            y - radius > taskbar.Top + taskbar.Height * .77) return false;
        foreach (Rect button in obstacles)
            if (x + radius > button.Left && x - radius < button.Right) return true;
        return false;
    }

    private enum ControlKind { App, Other, Tray, TrayButton }
    private record struct ControlBounds(Rect Bounds, ControlKind Kind);
    private record struct Flake(double X, double Y, double Speed, double Radius, double Drift, double Phase, bool Steer);
}
