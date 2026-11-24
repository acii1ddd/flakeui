using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

internal sealed unsafe partial class DirectRain : IDisposable
{
    // COM method slots from dxgi1_2.h, dcomp.h and d2d1.h, including IUnknown.
    private const int PresentSlot = 8, GetBufferSlot = 9, CreateSwapChainSlot = 24;
    private const int SetMaximumFrameLatencySlot = 31, GetFrameLatencyHandleSlot = 33;
    private const int FrameLatencyWaitableObject = 64;
    private const int CommitSlot = 3, CreateTargetSlot = 6, CreateVisualSlot = 7;
    private const int SetRootSlot = 3, SetContentSlot = 15;
    private const int CreateSurfaceTargetSlot = 15, CreateBrushSlot = 8, DrawLineSlot = 15;
    private const int ClearSlot = 47, BeginDrawSlot = 48, EndDrawSlot = 49;
    private const int Bgra8 = 87, Premultiplied = 1;
    private const int StillDrawing = unchecked((int)0x887A000A);

    private const string OverlayClassName = "NeonRain.TransparentOverlay";
    private static readonly WindowProcedure DefaultWindowProcedure = DefWindowProc;
    private static readonly nint Module = GetModuleHandle(null);
    private static ushort windowClass;

    private nint window;
    private nint device, dxgiDevice, factory, swap, surface, composition, target, visual;
    private nint d2d, render, glow, rain, spark, frameHalo, frameColor, frameHighlight, snowBrush, snowGeometry;
    private EventWaitHandle? frameReady;
    public WaitHandle FrameReady => frameReady!;
    public nint Handle => window;
    public int Presented { get; private set; }
    public int Busy { get; private set; }

    public DirectRain(System.Drawing.Rectangle bounds)
    {
        try
        {
            const uint overlayStyles = 0x08000000 | 0x00080000 | 0x80 | 0x20;
            EnsureWindowClass();
            window = CreateWindowEx(overlayStyles, OverlayClassName, "flakeui",
                0x88000000, bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0, 0, Module, 0);
            if (window == 0)
                throw new COMException($"Could not create the DirectX window (Win32 error {Marshal.GetLastWin32Error()}).");
            if (!SetLayeredWindowAttributes(window, 0, 255, 3))
                throw new COMException($"Could not enable mouse pass-through (Win32 error {Marshal.GetLastWin32Error()}).");
            Initialize(bounds.Width, bounds.Height);
            Native.SetWindowPos(Handle, new nint(-1), bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x50);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private static void EnsureWindowClass()
    {
        if (windowClass != 0) return;

        var description = new WindowClass
        {
            Procedure = Marshal.GetFunctionPointerForDelegate(DefaultWindowProcedure),
            Instance = Module,
            Background = GetStockObject(4), // BLACK_BRUSH, matching the layered window's color key.
            Name = OverlayClassName
        };
        windowClass = RegisterClass(in description);
        if (windowClass == 0)
            throw new COMException($"Could not register the DirectX window (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    private void Initialize(int width, int height)
    {
        Check(D3D11CreateDevice(0, 1, 0, 0x20, 0, 0, 7, out device, 0, 0), "D3D11CreateDevice");
        var dxgiId = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        Check(Marshal.QueryInterface(device, in dxgiId, out dxgiDevice), "IDXGIDevice");
        var factoryId = new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
        Check(CreateDXGIFactory2(0, in factoryId, out factory), "CreateDXGIFactory2");
        var desc = new SwapDescription
        {
            Width = width, Height = height, Format = Bgra8, Samples = 1, Usage = 0x20,
            Count = 2, Effect = 3, Alpha = Premultiplied, Flags = FrameLatencyWaitableObject
        };
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, SwapDescription*, nint, nint*, int>)Slot(factory, CreateSwapChainSlot))
            (factory, device, &desc, 0, &result), "CreateSwapChainForComposition");
        swap = result;
        ConfigureFramePacing();

        var surfaceId = new Guid("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
        Check(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swap, GetBufferSlot))
            (swap, 0, &surfaceId, &result), "GetBuffer");
        surface = result;
        var d2dId = new Guid("06152247-6f50-465a-9245-118bfd3b6007");
        Check(D2D1CreateFactory(0, in d2dId, 0, out d2d), "D2D1CreateFactory");
        var properties = new TargetProperties { Type = 2, Format = Bgra8, Alpha = Premultiplied, DpiX = 96, DpiY = 96 };
        Check(((delegate* unmanaged[Stdcall]<nint, nint, TargetProperties*, nint*, int>)Slot(d2d, CreateSurfaceTargetSlot))
            (d2d, surface, &properties, &result), "CreateDxgiSurfaceRenderTarget");
        render = result;
        rain = MakeBrush(new Color(46, 191, 255, 130));
        glow = MakeBrush(new Color(53, 163, 255, 38));
        spark = MakeBrush(new Color(225, 154, 255, 235));
        frameHalo = MakeBrush(new Color(155, 82, 255, 20));
        frameColor = MakeBrush(new Color(140, 111, 255, 190));
        frameHighlight = MakeBrush(new Color(134, 238, 255, 255));
        snowBrush = MakeBrush(new Color(222, 241, 255, 185));

        var compId = new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
        Check(DCompositionCreateDevice(dxgiDevice, in compId, out composition), "DCompositionCreateDevice");
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(composition, CreateTargetSlot))
            (composition, Handle, 1, &result), "CreateTargetForHwnd");
        target = result;
        Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(composition, CreateVisualSlot))
            (composition, &result), "CreateVisual");
        visual = result;
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(visual, SetContentSlot))(visual, swap), "SetContent");
        Check(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(target, SetRootSlot))(target, visual), "SetRoot");
        Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(composition, CommitSlot))(composition), "Commit");
    }

    private void ConfigureFramePacing()
    {
        var swap2Id = new Guid("a8be2ac4-199f-4946-b331-79599fb98de7");
        Check(Marshal.QueryInterface(swap, in swap2Id, out nint swap2), "IDXGISwapChain2");
        try
        {
            Check(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(swap2, SetMaximumFrameLatencySlot))
                (swap2, 1), "SetMaximumFrameLatency");
            nint handle = ((delegate* unmanaged[Stdcall]<nint, nint>)Slot(swap2, GetFrameLatencyHandleSlot))(swap2);
            if (handle == 0) throw new COMException("DXGI returned no frame latency handle.");
            frameReady = new EventWaitHandle(false, EventResetMode.AutoReset);
            frameReady.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        }
        finally { Marshal.Release(swap2); }
    }

    public void ResetCounters() => Presented = Busy = 0;

    public void StartFrame()
    {
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(render, BeginDrawSlot))(render);
        Color clear = default;
        ((delegate* unmanaged[Stdcall]<nint, Color*, void>)Slot(render, ClearSlot))(render, &clear);
    }

    public void Line(float x, float y, float endX, float endY, bool isSpark, bool light, float scale)
    {
        var draw = (delegate* unmanaged[Stdcall]<nint, PointF, PointF, nint, float, nint, void>)Slot(render, DrawLineSlot);
        var start = new PointF(x * scale, y * scale);
        var end = new PointF(endX * scale, endY * scale);
        if (!light) draw(render, start, end, glow, 5 * scale, 0);
        draw(render, start, end, isSpark ? spark : rain, (isSpark ? 2 : 1.4f) * scale, 0);
    }

    internal void FrameStroke(System.Windows.Point start, System.Windows.Point end, float scale, float accent, float intensity = 1)
    {
        var draw = (delegate* unmanaged[Stdcall]<nint, PointF, PointF, nint, float, nint, void>)Slot(render, DrawLineSlot);
        var a = new PointF((float)start.X * scale, (float)start.Y * scale);
        var b = new PointF((float)end.X * scale, (float)end.Y * scale);
        if (accent <= 0)
        {
            draw(render, a, b, frameHalo, (5 + 10 * intensity) * scale, 0);
            if (intensity > .05f)
                draw(render, a, b, frameHalo, 7 * intensity * scale, 0);
            draw(render, a, b, frameColor, (.65f + 1.05f * intensity) * scale, 0);
        }
        else
        {
            const int SetBrushOpacity = 4;
            ((delegate* unmanaged[Stdcall]<nint, float, void>)Slot(frameHighlight, SetBrushOpacity))(frameHighlight, accent);
            draw(render, a, b, glow, (4 + 5 * accent) * scale, 0);
            draw(render, a, b, frameHighlight, 2.4f * scale, 0);
        }
    }

    private readonly nint[] pondBrushes = new nint[3];
    internal void PondOval(float x,float y,float rx,float ry,int tint,float opacity,bool outline,float scale)
    {
        ref var brush = ref pondBrushes[tint];
        if(brush==0) brush=MakeBrush(tint switch { 0=>new Color(38,38,62,255), 1=>new Color(180,170,205,255), _=>new Color(255,220,144,255) });
        ((delegate* unmanaged[Stdcall]<nint,float,void>)Slot(brush,4))(brush,opacity);
        if(!outline) { FillEllipse(x,y,rx,ry,brush); return; }
        var ellipse=new Ellipse{X=x,Y=y,RadiusX=rx,RadiusY=ry};
        ((delegate* unmanaged[Stdcall]<nint,Ellipse*,nint,float,nint,void>)Slot(render,20))(render,&ellipse,brush,.8f*scale,0);
    }

    internal void RainBead(float x, float y, float radius) =>
        FillEllipse(x, y, radius, radius * .57f, rain);

    private nint petalGeometry, petalBrush;
    private readonly nint[] cursorBrushes = new nint[4];
    internal void DrawCursor(CursorScene scene,System.Drawing.Rectangle bounds,float scale)
    {
        if(scene.Style==CursorStyle.Petals){DrawPetals(scene.Petals,bounds,scale);return;}
        if(cursorBrushes[0]==0)
        {
            cursorBrushes[0]=MakeBrush(new Color(132,78,109,255));
            cursorBrushes[1]=MakeBrush(new Color(245,145,187,255));
            cursorBrushes[2]=MakeBrush(new Color(231,162,67,255));
            cursorBrushes[3]=MakeBrush(new Color(255,221,141,255));
        }
        var draw=(delegate* unmanaged[Stdcall]<nint,PointF,PointF,nint,float,nint,void>)Slot(render,DrawLineSlot);
        foreach(var m in scene.Marks)
        {
            float x=(float)(m.A.X-bounds.Left), y=(float)(m.A.Y-bounds.Top);
            float bx=(float)(m.B.X-bounds.Left),by=(float)(m.B.Y-bounds.Top),size=(float)m.Size;
            if(Math.Max(x,bx)<-size || Math.Min(x,bx)>bounds.Width+size ||
               Math.Max(y,by)<-size || Math.Min(y,by)>bounds.Height+size)continue;
            var brush=cursorBrushes[(int)m.Tint];
            ((delegate* unmanaged[Stdcall]<nint,float,void>)Slot(brush,4))(brush,(float)m.Alpha);
            if(m.Dot)FillEllipse(x,y,size,size,brush);
            else draw(render,new PointF(x,y),new PointF(bx,by),brush,size,0);
        }
    }
    private void DrawPetals(PetalSwarm swarm, System.Drawing.Rectangle bounds, float scale)
    {
        if (petalGeometry == 0)
        {
            petalBrush = MakeBrush(new Color(246,153,193,245));
            nint sink = 0;
            try
            {
                nint geometry;
                Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Slot(d2d,10))(d2d,&geometry),"Petal path");
                petalGeometry = geometry;
                Check(((delegate* unmanaged[Stdcall]<nint,nint*,int>)Slot(geometry,17))(geometry,&sink),"Open petal");
                var shape = PetalSwarm.Outline;
                ((delegate* unmanaged[Stdcall]<nint,PointF,int,void>)Slot(sink,5))(sink,new PointF((float)shape[0].X,(float)shape[0].Y),0);
                PointF* points = stackalloc PointF[shape.Length-1];
                for(int i=1;i<shape.Length;i++) points[i-1]=new PointF((float)shape[i].X,(float)shape[i].Y);
                ((delegate* unmanaged[Stdcall]<nint,PointF*,uint,void>)Slot(sink,6))(sink,points,(uint)shape.Length-1);
                ((delegate* unmanaged[Stdcall]<nint,int,void>)Slot(sink,8))(sink,1);
                Check(((delegate* unmanaged[Stdcall]<nint,int>)Slot(sink,9))(sink),"Close petal");
            }
            finally { Release(ref sink); }
        }
        var transform=(delegate* unmanaged[Stdcall]<nint,float*,void>)Slot(render,30);
        float* matrix=stackalloc float[6];
        foreach(ref readonly var p in swarm.Petals.AsSpan())
        {
            float alpha=(float)(p.Alpha*p.Visibility);
            float x=(float)(p.Position.X-bounds.Left), y=(float)(p.Position.Y-bounds.Top);
            if(alpha<.01 || x < -7*scale || y < -7*scale || x>bounds.Width+7*scale || y>bounds.Height+7*scale) continue;
            float c=(float)Math.Cos(p.Angle)*scale, s=(float)Math.Sin(p.Angle)*scale;
            matrix[0]=c; matrix[1]=s; matrix[2]=-s*(float)p.Width; matrix[3]=c*(float)p.Width; matrix[4]=x; matrix[5]=y;
            transform(render,matrix);
            ((delegate* unmanaged[Stdcall]<nint,float,void>)Slot(petalBrush,4))(petalBrush,alpha);
            ((delegate* unmanaged[Stdcall]<nint,nint,nint,nint,void>)Slot(render,23))(render,petalGeometry,petalBrush,0);
        }
        matrix[0]=matrix[3]=1; matrix[1]=matrix[2]=matrix[4]=matrix[5]=0;
        transform(render,matrix);
    }

    internal void SnowDot(float x, float y, float radius) =>
        FillEllipse(x, y, radius, radius, snowBrush);

    private void FillEllipse(float x, float y, float radiusX, float radiusY, nint brush)
    {
        const int FillEllipseSlot = 21;
        var ellipse = new Ellipse { X = x, Y = y, RadiusX = radiusX, RadiusY = radiusY };
        ((delegate* unmanaged[Stdcall]<nint, Ellipse*, nint, void>)Slot(render, FillEllipseSlot))(render, &ellipse, brush);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Ellipse
    {
        public float X, Y, RadiusX, RadiusY;
    }

    internal void DrawSnowBank(ReadOnlySpan<float> ridge, float floor, float left, float width, float scale, bool changed)
    {
        const int CreatePathGeometry = 10, OpenGeometry = 17;
        const int BeginFigure = 5, AddLines = 6, EndFigure = 8, CloseSink = 9;
        const int FillGeometry = 23;
        if (changed)
        {
            Release(ref snowGeometry);
            nint geometry = 0, sink = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(d2d, CreatePathGeometry))
                    (d2d, &geometry), "Create snow geometry");
                Check(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(geometry, OpenGeometry))
                    (geometry, &sink), "Open snow geometry");

                Span<PointF> points = stackalloc PointF[ridge.Length + 3];
                float bottom = floor * scale;
                points[0] = new PointF(left * scale, (floor - ridge[0]) * scale);
                for (int i = 0; i < ridge.Length; i++)
                    points[i + 1] = new PointF((left + width * (i + .5f) / ridge.Length) * scale, (floor - ridge[i]) * scale);
                points[^2] = new PointF((left + width) * scale, (floor - ridge[^1]) * scale);
                points[^1] = new PointF((left + width) * scale, bottom);
                ((delegate* unmanaged[Stdcall]<nint, PointF, int, void>)Slot(sink, BeginFigure))
                    (sink, new PointF(left * scale, bottom), 0);
                fixed (PointF* data = points)
                    ((delegate* unmanaged[Stdcall]<nint, PointF*, uint, void>)Slot(sink, AddLines))
                        (sink, data, (uint)points.Length);
                ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(sink, EndFigure))(sink, 1);
                Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(sink, CloseSink))(sink), "Close snow geometry");
                (snowGeometry, geometry) = (geometry, 0);
            }
            finally
            {
                Release(ref sink);
                Release(ref geometry);
            }
        }

        if (snowGeometry != 0)
            ((delegate* unmanaged[Stdcall]<nint, nint, nint, nint, void>)Slot(render, FillGeometry))
                (render, snowGeometry, snowBrush, 0);
    }

    public void FinishFrame()
    {
        Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, int>)Slot(render, EndDrawSlot))(render, 0, 0), "EndDraw");
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(swap, PresentSlot))(swap, 1, 8);
        if (result == StillDrawing) Busy++;
        else
        {
            Check(result, "Present");
            if (result == 0) Presented++;
        }
    }

    private nint MakeBrush(Color color)
    {
        nint result;
        Check(((delegate* unmanaged[Stdcall]<nint, Color*, nint, nint*, int>)Slot(render, CreateBrushSlot))
            (render, &color, 0, &result), "CreateSolidColorBrush");
        return result;
    }

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];
    private static void Check(int status, string name)
    {
        if (status < 0) throw new COMException($"{name} failed (0x{status:X8})", status);
    }

    public void Dispose()
    {
        frameReady?.Dispose();
        frameReady = null;
        Release(ref snowGeometry);
        for(int i=0;i<cursorBrushes.Length;i++) Release(ref cursorBrushes[i]);
        for(int i=0;i<pondBrushes.Length;i++) Release(ref pondBrushes[i]);
        Release(ref petalBrush);
        Release(ref petalGeometry);
        Release(ref snowBrush);
        Release(ref frameHighlight);
        Release(ref frameColor);
        Release(ref frameHalo);
        Release(ref spark);
        Release(ref glow);
        Release(ref rain);
        Release(ref render);
        Release(ref d2d);
        Release(ref visual);
        Release(ref target);
        Release(ref composition);
        Release(ref surface);
        Release(ref swap);
        Release(ref factory);
        Release(ref dxgiDevice);
        Release(ref device);
        if (window != 0) DestroyWindow(window);
        window = 0;
    }

    private static void Release(ref nint pointer)
    {
        if (pointer != 0) Marshal.Release(pointer);
        pointer = 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SwapDescription
    {
        public int Width, Height, Format, Stereo, Samples, Quality, Usage, Count, Scaling, Effect, Alpha, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TargetProperties
    {
        public int Type, Format, Alpha;
        public float DpiX, DpiY;
        public int Usage, MinimumLevel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PointF(float x, float y)
    {
        public readonly float X = x, Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Color(byte r, byte g, byte b, byte a)
    {
        public readonly float R = r / 255f, G = g / 255f, B = b / 255f, A = a / 255f;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        public uint Style;
        public nint Procedure;
        public int ClassExtra, WindowExtra;
        public nint Instance, Icon, Cursor, Background, MenuName;
        public string Name;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern nint DefWindowProc(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(in WindowClass description);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? name);
    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driver, nint software, uint flags,
        nint levels, uint levelCount, uint sdk, out nint device, nint level, nint context);
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory2(uint flags, in Guid iid, out nint factory);
    [DllImport("d2d1.dll")]
    private static extern int D2D1CreateFactory(int type, in Guid iid, nint options, out nint factory);
    [DllImport("dcomp.dll")]
    private static extern int DCompositionCreateDevice(nint dxgi, in Guid iid, out nint device);
}
