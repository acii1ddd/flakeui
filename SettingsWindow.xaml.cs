using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace FlakeUI;

internal partial class SettingsWindow : Window
{
    private readonly RainPreferences preferences;
    private readonly Action changed;
    private readonly Action<NativeEffect> toggle;
    private NativeEffect tab = NativeEffect.Rain, effects;
    private double jumpPhase, jumpDuration, restRemaining, snowMass, snowfall, spilling;
    private bool right;
    private int flakeIndex;
    private readonly Ellipse[] flakes = new Ellipse[28];
    private readonly double[] flakeAge = new double[28];
    private readonly double[] flakeX = new double[28];
    private readonly bool[] sliding = new bool[28];
    private readonly Random random = new();
    private bool ready, ticking;
    private readonly Stopwatch clock = new();
    private double last, phase, sinceImpact = 2;

    internal SettingsWindow(RainPreferences preferences, Action changed, Action<NativeEffect> toggle)
    {
        this.preferences = preferences;
        this.changed = changed;
        this.toggle = toggle;
        InitializeComponent();
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("flakeui.ico")!;
        var icon = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Icon = icon.Frames[^1];
        for (int i = 0; i < flakes.Length; i++)
        {
            var dot = new Ellipse { Width = 3, Height = 3, Fill = Brushes.AliceBlue, Stroke = Brushes.LightSlateGray, StrokeThickness = .5, Opacity = 0 };
            flakes[i] = dot;
            flakeAge[i] = -1;
            SnowCanvas.Children.Add(dot);
        }
        SelectTab(NativeEffect.Rain);
        ApplyPalette();

        ready = true;
        Loaded += (_, _) => UpdateClock();
        IsVisibleChanged += (_, _) => UpdateClock();
        StateChanged += (_, _) => UpdateClock();
        Closed += (_, _) => StopClock();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    internal void SyncEffects(NativeEffect current)
    {
        effects = current;
        Rain.IsChecked = (effects & tab) != 0;
    }

    private void TabClick(object sender, RoutedEventArgs e) =>
        SelectTab(Enum.Parse<NativeEffect>((string)((Button)sender).Tag));

    private void SelectTab(NativeEffect selected)
    {
        ready = false;
        tab = selected;
        bool runner = tab == NativeEffect.Runner, snow = tab == NativeEffect.Snow;
        EffectTitle.Text = runner ? "Раннер" : snow ? "Снег" : "Неоновый дождь";
        FirstLabel.Text = runner ? "Скорость прыжка" : snow ? "Высота сугробов" : "Скорость";
        SecondLabel.Text = runner ? "Пауза после посадки" : "Интенсивность";
        System.Windows.Automation.AutomationProperties.SetName(Rain, EffectTitle.Text);
        System.Windows.Automation.AutomationProperties.SetName(Speed, FirstLabel.Text);
        System.Windows.Automation.AutomationProperties.SetName(Density, SecondLabel.Text);
        RainTab.FontWeight = tab == NativeEffect.Rain ? FontWeights.Bold : FontWeights.Normal;
        RunnerTab.FontWeight = runner ? FontWeights.Bold : FontWeights.Normal;
        SnowTab.FontWeight = snow ? FontWeights.Bold : FontWeights.Normal;
        RainPreview.Visibility = tab == NativeEffect.Rain ? Visibility.Visible : Visibility.Collapsed;
        RunnerPreview.Visibility = runner ? Visibility.Visible : Visibility.Collapsed;
        SnowPreview.Visibility = snow ? Visibility.Visible : Visibility.Collapsed;
        Density.Minimum = runner ? 50 : 10;
        Density.Maximum = tab == NativeEffect.Rain ? 100 : 150;
        Speed.Value = runner ? preferences.JumpSpeed : snow ? preferences.SnowHeight : preferences.Speed;
        Density.Value = runner ? preferences.LandingRest : snow ? preferences.SnowIntensity : preferences.Intensity;
        Labels();
        SyncEffects(effects);
        ready = true;
    }
    private Native.POINT pressPoint;
    private Native.RECT pressBounds;
    private nint dragHandle;
    private bool pastDragThreshold;
    private int lastDragX, lastDragY;
    private double dragThresholdX, dragThresholdY;

    private void BeginHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        var header = (UIElement)sender;
        var handle = new WindowInteropHelper(this).Handle;
        if (!Native.GetCursorPos(out pressPoint) || !Native.GetWindowRect(handle, out pressBounds)
            || !header.CaptureMouse()) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        dragThresholdX = SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX;
        dragThresholdY = SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY;
        lastDragX = pressBounds.Left;
        lastDragY = pressBounds.Top;
        dragHandle = handle;
        pastDragThreshold = false;
        e.Handled = true;
    }

    private void MoveHeader(object sender, MouseEventArgs e)
    {
        if (dragHandle == 0 || e.LeftButton != MouseButtonState.Pressed) return;
        PositionHeader();
    }

    private void PositionHeader()
    {
        if (!Native.GetCursorPos(out var point)) return;
        int dx = point.X - pressPoint.X, dy = point.Y - pressPoint.Y;
        if (!pastDragThreshold && Math.Abs(dx) < dragThresholdX && Math.Abs(dy) < dragThresholdY) return;
        pastDragThreshold = true;
        int x = pressBounds.Left + dx, y = pressBounds.Top + dy;
        if (x == lastDragX && y == lastDragY) return;
        // DragMove enters a nested Windows move loop on the dispatcher that presents DirectX frames.
        const uint keepSizeZOrderAndFocus = 0x0001 | 0x0004 | 0x0010;
        if (Native.SetWindowPos(dragHandle, 0, x, y, 0, 0, keepSizeZOrderAndFocus))
            (lastDragX, lastDragY) = (x, y);
    }

    private void EndHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (dragHandle == 0) return;
        PositionHeader();
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void HeaderCaptureLost(object sender, MouseEventArgs e) => dragHandle = 0;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void RainClick(object sender, RoutedEventArgs e) => toggle(tab);
    private void LightClick(object sender, RoutedEventArgs e) => SetTheme(false);
    private void DarkClick(object sender, RoutedEventArgs e) => SetTheme(true);
    private void SetTheme(bool night)
    {
        if (preferences.Night == night) return;
        preferences.Night = night;
        ApplyPalette();
        changed();
    }

    private void ApplyPalette()
    {
        bool night = preferences.Night;
        void Paint(string key, string light, string dark)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(night ? dark : light));
            brush.Freeze();
            Resources[key] = brush;
        }
        Paint("Paper", "#FFF5E8", "#1C101E");
        Paint("Ink", "#402D30", "#F6ECE6");
        Paint("Line", "#DCC7B8", "#613C58");
        Paint("Rim", "#B87B78", "#AB6C91");
        Paint("Wash", "#F3DCCF", "#40263F");
        Paint("Accent", "#963847", "#D5A1EF");
        Paint("Petal", "#DF9295", "#EBA9D0");
        Paint("Wood", "#795548", "#A9738C");
        string theme = night ? "dark" : "light";
        Logo.Source = Artwork("logo", theme);
        Branch.Source = Artwork("branch", theme);
        DayMark.SetResourceReference(Border.BackgroundProperty, night ? "Paper" : "Wash");
        NightMark.SetResourceReference(Border.BackgroundProperty, night ? "Rim" : "Paper");
    }

    private static BitmapImage Artwork(string part, string theme) =>
        new(new Uri($"pack://application:,,,/NeonRainOverlay;component/Assets/{part}-{theme}.png"));

    private void Labels()
    {
        SpeedValue.Text = $"{Math.Round(Speed.Value)} %";
        DensityValue.Text = $"{Math.Round(Density.Value)} %";
    }
    private void SliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready) return;
        int speed = (int)Math.Round(Speed.Value);
        int intensity = (int)Math.Round(Density.Value);
        switch (tab)
        {
            case NativeEffect.Runner: (preferences.JumpSpeed, preferences.LandingRest) = (speed, intensity); break;
            case NativeEffect.Snow: (preferences.SnowHeight, preferences.SnowIntensity) = (speed, intensity); break;
            default: (preferences.Speed, preferences.Intensity) = (speed, intensity); break;
        }
        Labels();
        changed();
    }
    private void ResetClick(object sender, RoutedEventArgs e) { Speed.Value = 100; Density.Value = 100; }

    private void UpdateClock()
    {
        if (!IsLoaded || !IsVisible || WindowState == WindowState.Minimized) { StopClock(); return; }
        if (ticking) return;
        clock.Restart(); last = 0; ticking = true;
        CompositionTarget.Rendering += Animate;
    }
    private void StopClock()
    {
        if (!ticking) return;
        CompositionTarget.Rendering -= Animate;
        ticking = false; clock.Stop();
    }
    private void AnimateRunner(double dt)
    {
        if (restRemaining > 0) restRemaining -= dt;
        else
        {
            if (jumpDuration == 0) jumpDuration = 1.1 * 100 / preferences.JumpSpeed;
            jumpPhase += dt / jumpDuration;
            if (jumpPhase >= 1)
            {
                right = !right;
                jumpPhase = jumpDuration = 0;
                restRemaining = RunnerScene.LandingPause(1.1, .32) * preferences.LandingRest / 100;
                LandingGlint.Opacity = .85;
            }
        }
        double t = jumpPhase * jumpPhase * (3 - 2 * jumpPhase);
        Canvas.SetLeft(Ship, 72 + 108 * (right ? 1 - t : t));
        Canvas.SetTop(Ship, 46 - 33 * Math.Sin(Math.PI * jumpPhase));
        Canvas.SetLeft(LandingGlint, (right ? 180 : 72) - 10);
        Canvas.SetTop(LandingGlint, 48);
        LandingGlint.Opacity = Math.Max(0, LandingGlint.Opacity - 3 * dt);
    }

    private const double SnowLeft = 82, SnowRight = 175;
    private const int SnowSegments = 48;
    private readonly PointCollection capPoints = new();
    private double drawnMass = -1;

    // Invert X on the two quadratic curves forming the upper rim of the petal.
    private static double PetalY(double x)
    {
        if (x <= 155)
        {
            double t = 2 * (x - 65) / (76 + Math.Sqrt(76 * 76 + 56 * (x - 65)));
            return 46 + t * (24 - 26 * t);
        }
        double u = 2 * (x - 155) / (38 + Math.Sqrt(38 * 38 - 20 * (x - 155)));
        return 44 + u * (-8 + 3 * u);
    }

    private double SnowY(double x)
    {
        double u = Math.Clamp((x - SnowLeft) / (SnowRight - SnowLeft), 0, 1);
        double mound = 11 * Math.Sin(Math.PI * u) * (1 + .15 * Math.Cos(12 * u));
        return PetalY(x) - snowMass * mound;
    }

    private void DrawSnowCap()
    {
        if (drawnMass == snowMass) return;
        if (capPoints.Count == 0)
        {
            var contour = new PathFigure { StartPoint = new Point(SnowLeft, PetalY(SnowLeft)), IsClosed = true };
            for (int i = 1; i <= SnowSegments; i++) capPoints.Add(default);
            contour.Segments.Add(new PolyLineSegment { Points = capPoints });
            var basePoints = new PointCollection();
            for (int i = SnowSegments - 1; i > 0; i--)
            {
                double x = SnowLeft + (SnowRight - SnowLeft) * i / SnowSegments;
                basePoints.Add(new Point(x, PetalY(x)));
            }
            contour.Segments.Add(new PolyLineSegment { Points = basePoints });
            SnowCap.Data = new PathGeometry(new[] { contour });
        }
        for (int i = 1; i <= SnowSegments; i++)
        {
            double x = SnowLeft + (SnowRight - SnowLeft) * i / SnowSegments;
            capPoints[i - 1] = new Point(x, SnowY(x));
        }
        SnowCap.Opacity = Math.Clamp(snowMass * 8, 0, 1);
        drawnMass = snowMass;
    }

    private void EmitFlake(bool slide)
    {
        int i = flakeIndex++ % flakes.Length;
        sliding[i] = slide;
        flakeAge[i] = 0;
        flakeX[i] = slide ? SnowRight - 2 + random.NextDouble() * 2 : 87 + random.NextDouble() * 72;
    }

    private void AnimateSnow(double dt)
    {
        snowfall += dt * 4 * preferences.SnowIntensity / 100;
        if (snowfall >= 1) { snowfall--; EmitFlake(false); }
        double limit = preferences.SnowHeightScale;
        if (spilling <= 0 && snowMass >= limit) spilling = snowMass * .68;
        if (spilling > 0)
        {
            double lost = Math.Min(spilling, dt * .9);
            if ((int)(spilling * 14) != (int)((spilling - lost) * 14)) EmitFlake(true);
            spilling -= lost;
            snowMass -= lost;
        }
        DrawSnowCap();
        for (int i = 0; i < flakes.Length; i++)
        {
            if (flakeAge[i] < 0) continue;
            double t = flakeAge[i] += dt;
            bool slide = sliding[i];
            double x = flakeX[i] + (slide ? 23 * t : 3 * Math.Sin(t * 3 + i));
            double y = slide ? PetalY(flakeX[i]) + 29 * t * t : 2 + 31 * t;
            bool landed = !slide && y + flakes[i].Height >= SnowY(x);
            if (landed || y > 74)
            {
                if (landed) snowMass += .056;
                flakeAge[i] = -1;
                flakes[i].Opacity = 0;
                continue;
            }
            flakes[i].Opacity = slide ? Math.Clamp((74 - y) / 12, 0, 1) : .9;
            Canvas.SetLeft(flakes[i], x);
            Canvas.SetTop(flakes[i], y);
        }
    }

    private void Animate(object? sender, EventArgs e)
    {
        double now = clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now-last,0,.05);
        last = now;
        if (tab == NativeEffect.Runner) { AnimateRunner(dt); return; }
        if (tab == NativeEffect.Snow) { AnimateSnow(dt); return; }
        double flight = .72 * 100 / preferences.Speed;
        phase += dt;
        sinceImpact += dt;
        if (phase >= flight && phase-dt < flight) sinceImpact = 0;
        if (phase >= flight + .22 + (100-preferences.Intensity)*.012) phase = 0;
        RaindropShape.Opacity = phase < flight ? .83 : 0;
        Fall.Y = 3 + 40 * Math.Pow(Math.Min(1,phase/flight),2);
        double pulse = Math.Sin(9*sinceImpact)*Math.Exp(-3.8*sinceImpact);
        Rock.Angle = -7 + 8*pulse; Bob.Y = 2.6*pulse;
        Wave.ScaleX = Wave.ScaleY = .10 + .95*Math.Min(1,sinceImpact/1.35);
        Ripple.Opacity = .57*Math.Max(0,1-sinceImpact/1.35);
    }
}
