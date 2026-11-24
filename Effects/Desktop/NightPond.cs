using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using Forms = System.Windows.Forms;

internal sealed class NightPond
{
    internal float X, Y, Alpha;
    private readonly (float x,long time)[] rings = new (float,long)[16];
    internal int Hits { get; private set; }
    private int next;
    internal void Crossing(double x0,double y0,double x1,double y1)
    {
        if (y0 >= Y || y1 < Y || y1 <= y0) return;
        float x=(float)(x0+(x1-x0)*(Y-y0)/(y1-y0));
        if (Math.Abs(x-X)>70) return;
        Hits++;
        rings[next]=(x,Environment.TickCount64);
        next=(next+1)%rings.Length;
    }
    internal void Draw(DirectRain gpu,float scale)
    {
        long now=Environment.TickCount64;
        double t=now*.001;
        void Oval(float x,float y,float rx,float ry,int color,float opacity,bool outline=false) =>
            gpu.PondOval(x*scale,y*scale,rx*scale,ry*scale,color,Alpha*opacity,outline,scale);
        Oval(X,Y,79,9,0,.90f);
        Oval(X,Y,76,7,1,.35f,true);
        for (int i=0;i<3;i++)
        {
            float x=X+(i-1)*42+(float)Math.Sin(t*.75+i*3)*12;
            float h=15+(float)Math.Sin(t*1.1+i*1.7)*8;
            float pulse=.72f+.2f*(float)Math.Sin(t*1.7+i);
            Oval(x,Y-h,5,5,2,.16f*pulse);
            Oval(x,Y-h,1.7f,1.7f,2,pulse);
            Oval(x+(float)Math.Sin(t*2+i),Y+2,3,h*.055f,2,.43f*pulse);
        }
        foreach(var ring in rings)
        {
            float age=(now-ring.time)/800f;
            if(ring.time!=0 && age>=0 && age<1) Oval(ring.x,Y,1+age*8,.5f+age*2,1,1-age,true);
        }
    }
}

internal sealed class PondController : IDisposable
{
    private readonly Forms.Timer timer=new(){Interval=500};
    private readonly Func<string,NativeRainSession?> existing;
    private readonly RainPreferences preferences;
    private NativeRainSession? owned, attached;
    private NightPond? pond;
    private Rectangle lastBounds;
    private Point lastMouse;
    private string activeScreen = "";
    private uint previousInput;
    private long previewEnd, transition;
    private bool fading;
    private float initialAlpha;
    internal NightPond? VisiblePond => pond;
    internal bool Previewing => previewEnd>Environment.TickCount64;

    internal PondController(RainPreferences preferences,Func<string,NativeRainSession?> existing)
    {
        this.preferences=preferences;
        this.existing=existing;
        timer.Tick+=(_,_)=>Tick();
        Configure();
    }
    internal void Configure()
    {
        if(preferences.NightPondEnabled || previewEnd!=0) timer.Start();
        else { timer.Stop(); Detach(); }
    }
    internal void Preview()
    {
        previewEnd=Environment.TickCount64+5000;
        timer.Start();
        Tick();
    }
    internal void CancelPreview() { previewEnd=0; BeginFade(); }
    private void BeginFade()
    {
        if(pond!=null && !fading){fading=true;transition=Environment.TickCount64;initialAlpha=pond.Alpha;}
    }
    private void Tick()
    {
        try { Update(); }
        catch(COMException e) { Trace.WriteLine(e); previewEnd=0; Detach(); timer.Interval=1000; }
    }
    private void Update()
    {
        long now=Environment.TickCount64;
        var input=new LastInput { Size=8 };
        if(!GetLastInputInfo(ref input)) { Detach(); return; }
        var mouse=Forms.Cursor.Position;
        nint foreground=Native.GetForegroundWindow();
        if(input.Time!=previousInput || activeScreen.Length==0)
        {
            activeScreen=(mouse!=lastMouse || foreground==0 || activeScreen.Length==0 ? Forms.Screen.FromPoint(mouse) : Forms.Screen.FromHandle(foreground)).DeviceName;
            previousInput=input.Time;
            lastMouse=mouse;
        }
        if(previewEnd!=0 && (now>=previewEnd || DesktopSwitcher.GetAsyncKeyState(27)<0)) previewEnd=0;
        uint idle=unchecked((uint)now-input.Time);
        bool wanted=previewEnd!=0 || preferences.NightPondEnabled && idle>=60000;
        var screen=Array.Find(Forms.Screen.AllScreens,s=>s.DeviceName==activeScreen) ?? Forms.Screen.FromPoint(mouse);
        if(wanted && Fullscreen(foreground,screen.Bounds)) wanted=false;
        if(!wanted)
        {
            BeginFade();
            if(pond!=null)pond.Alpha=initialAlpha*Math.Max(0,1-(now-transition)/250f);
            if(pond==null || pond.Alpha<=0) { Detach(); timer.Interval=500; if(!preferences.NightPondEnabled)timer.Stop(); }
            return;
        }
        var shared=existing(screen.DeviceName);
        if(pond==null || lastBounds!=screen.Bounds || (shared??owned)!=attached || attached?.Stopped==true)
        {
            Detach();
            attached=shared ?? (owned=new NativeRainSession(_=>Detach(),NativeEffect.None,screen.Bounds));
            lastBounds=screen.Bounds;
            pond=new NightPond();
            attached.Pond=pond;
            transition=now;
        }
        if(fading)transition=now-(long)(pond.Alpha*250);
        fading=false;
        pond.Alpha=Math.Min(1,(now-transition)/250f);
        timer.Interval=32;
        if(now<nextPlacement && lastPlacementScreen==screen.DeviceName)return;
        nextPlacement=now+250;
        lastPlacementScreen=screen.DeviceName;
        if(!TryAnchor(screen,attached!.Dpi,out pond.X,out pond.Y)) { Detach(); timer.Interval=500; }
    }
    private long nextPlacement;
    private string lastPlacementScreen="";
    internal static bool TryAnchor(Forms.Screen screen,uint dpi,out float x,out float y)
    {
        var bar=Native.FindWindow("Shell_TrayWnd",null);
        var bounds=screen.Bounds;
        for(nint secondary=0;bar!=0;bar=secondary=FindWindowExW(0,secondary,"Shell_SecondaryTrayWnd",null))
        {
            if(!Native.IsWindowVisible(bar) || !Native.GetWindowRect(bar,out var r))continue;
            var clipped=Rectangle.Intersect(bounds,Rectangle.FromLTRB(r.Left,r.Top,r.Right,r.Bottom));
            if(clipped.Width<=2 || clipped.Height<=2)continue;
            Anchor(bounds,clipped,dpi,out x,out y);
            return true;
        }
        x=y=0;return false;
    }
    internal static void Anchor(Rectangle bounds,Rectangle clipped,uint dpi,out float x,out float y)
    {
            float scale=dpi/96f;
            bool horizontal=clipped.Width>clipped.Height;
            x=(horizontal ? clipped.Right-bounds.Left-88*scale :
                clipped.Left==bounds.Left ? clipped.Right-bounds.Left+84*scale : clipped.Left-bounds.Left-84*scale)/scale;
            y=(horizontal ? clipped.Top==bounds.Top ? clipped.Bottom-bounds.Top+31*scale : clipped.Top-bounds.Top-11*scale :
                clipped.Bottom-bounds.Top-15*scale)/scale;
    }
    private static bool Fullscreen(nint window,Rectangle bounds)
    {
        if(window==0 || window==Native.GetShellWindow() || window==Native.GetDesktopWindow() || !Native.GetWindowRect(window,out var r))return false;
        var name=new System.Text.StringBuilder(80);
        GetClassNameW(window,name,name.Capacity);
        return name.ToString() is not ("WorkerW" or "Progman") && r.Left<=bounds.Left && r.Top<=bounds.Top && r.Right>=bounds.Right && r.Bottom>=bounds.Bottom;
    }
    private void Detach()
    {
        if(attached!=null)attached.Pond=null;
        owned?.Dispose(); owned=null; attached=null; pond=null; fading=false; nextPlacement=0;
    }
    public void Dispose(){timer.Dispose();Detach();}
    [StructLayout(LayoutKind.Sequential)] private struct LastInput { public uint Size,Time; }
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LastInput input);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern nint FindWindowExW(nint parent,nint after,string cls,string? title);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassNameW(nint window,System.Text.StringBuilder text,int max);
}
