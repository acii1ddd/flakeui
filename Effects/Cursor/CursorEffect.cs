using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

internal sealed class CursorEffect : IDisposable
{
    private readonly HwndSource source;
    private readonly RainPreferences preferences;
    internal readonly CursorScene Scene = new();
    internal int Impulses { get; private set; }
    private long last;
    private double scale = 1;
    internal CursorEffect(RainPreferences preferences)
    {
        this.preferences = preferences;
        source = new HwndSource(new HwndSourceParameters("flakeui cursor input") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        source.AddHook(Input);
        var device = new Device { Page = 1, Usage = 2, Flags = 0x100, Target = source.Handle };
        if (!RegisterRawInputDevices(ref device, 1, (uint)Marshal.SizeOf<Device>()))
        {
            int error = Marshal.GetLastWin32Error();
            source.Dispose();
            throw new COMException("Could not register cursor input", unchecked((int)0x80070000) | error);
        }
    }

    internal void Advance(long now, System.Drawing.Rectangle bounds, double dpiScale)
    {
        if (!Native.GetCursorPos(out var p)) return;
        if (bounds.Contains(p.X,p.Y)) scale = dpiScale;
        double dt = last == 0 ? 1.0/60 : Stopwatch.GetElapsedTime(last, now).TotalSeconds;
        if (dt < .002) return;
        last = now;
        Scene.Select(preferences.Style);
        Scene.Step(new Point(p.X,p.Y),dt,scale,preferences.TrailLength);
    }

    private unsafe nint Input(nint hwnd, int msg, nint w, nint data, ref bool handled)
    {
        if (msg != 0xFF) return 0;
        uint header = (uint)(8 + 2 * IntPtr.Size);
        byte* packet = stackalloc byte[64];
        uint size = 64;
        uint read = GetRawInputData(data, 0x10000003, packet, ref size, header);
        if (read == uint.MaxValue || read < header + 8 || *(uint*)packet != 0) return 0;
        ushort flags = *(ushort*)(packet + header + 4);
        if ((flags & 5) != 0 && Native.GetCursorPos(out var p))
        {
            Impulses += ((flags & 1) != 0 ? 1 : 0) + ((flags & 4) != 0 ? 1 : 0);
            var point = new Point(p.X,p.Y);
            if ((flags & 1) != 0) Scene.Click(point,false,preferences.ClickForce,scale);
            if ((flags & 4) != 0) Scene.Click(point,true,preferences.ClickForce,scale);
        }
        return 0;
    }

    public void Dispose()
    {
        var device = new Device { Page=1, Usage=2, Flags=1 };
        RegisterRawInputDevices(ref device,1,(uint)Marshal.SizeOf<Device>());
        source.RemoveHook(Input);
        source.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private struct Device { public ushort Page, Usage; public uint Flags; public nint Target; }
    [DllImport("user32", SetLastError=true)] private static extern bool RegisterRawInputDevices(ref Device device,uint count,uint size);
    [DllImport("user32")] private static extern unsafe uint GetRawInputData(nint data,uint command,byte* buffer,ref uint size,uint header);
}