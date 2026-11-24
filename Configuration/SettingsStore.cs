using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

internal sealed class RainPreferences
{
    public int Speed { get; set; } = 100;
    public int Intensity { get; set; } = 100;
    public int JumpSpeed { get; set; } = 100;
    public int LandingRest { get; set; } = 100;
    public int SnowHeight { get; set; } = 100;
    internal double SnowHeightScale
    {
        get
        {
            double linear = SnowHeight / 100.0;
            double aboveDefault = Math.Max(0, linear - 1);
            return linear + aboveDefault * aboveDefault;
        }
    }

    public int SnowIntensity { get; set; } = 100;
    public CursorStyle Style { get; set; }
    public int TrailLength { get; set; } = 100;
    public int ClickForce { get; set; } = 100;
    public bool DesktopSwitcherEnabled { get; set; }
    public bool NightPondEnabled { get; set; }
    public bool Night { get; set; }
}
internal sealed class SettingsStore : IDisposable
{
    private readonly string file;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(450) };
    internal RainPreferences Values { get; }
    internal SettingsStore(string? path = null)
    {
        file = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "flakeui", "settings.json");
        try { Values = JsonSerializer.Deserialize<RainPreferences>(File.ReadAllText(file)) ?? new(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Values = new(); }
        Values.Speed = Math.Clamp(Values.Speed,0,150);
        Values.Intensity = Math.Clamp(Values.Intensity,0,100);
        Values.JumpSpeed = Math.Clamp(Values.JumpSpeed, 0, 150);
        Values.LandingRest = Math.Clamp(Values.LandingRest, 0, 150);
        Values.SnowHeight = Math.Clamp(Values.SnowHeight, 0, 150);
        Values.SnowIntensity = Math.Clamp(Values.SnowIntensity, 0, 150);
        if (!Enum.IsDefined(Values.Style)) Values.Style = CursorStyle.Petals;
        Values.TrailLength = Math.Clamp(Values.TrailLength,0,150);
        Values.ClickForce = Math.Clamp(Values.ClickForce,0,150);
        timer.Tick += (_, _) => Save();
    }

    internal void Schedule() { timer.Stop(); timer.Start(); }
    private void Save()
    {
        timer.Stop();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file+".tmp",JsonSerializer.Serialize(Values));
            File.Move(file+".tmp",file,true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e.Message); }
    }
    public void Dispose() { if (timer.IsEnabled) Save(); }
}