using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

internal sealed class DesktopSwitcher : IDisposable
{
    private delegate nint KeyboardProc(int code, nint message, nint data);
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(nint window, out int current);
    }
    private delegate void ForegroundProc(nint hook,uint ev,nint window,int obj,int child,uint thread,uint time);
    private readonly ForegroundProc foregroundCallback;
    private readonly List<nint> recent = new();
    private nint foregroundHook;
    private IVirtualDesktopManager? desktops;
    private readonly KeyboardProc callback;
    private readonly RainPreferences preferences;
    private readonly Forms.Timer timer = new() { Interval = 16 };
    private readonly List<nint> windows = new();
    private SwitchCards? panel;
    private long started, lastRefresh;
    private int selected;
    private nint hook, origin;
    private bool active, eatTabUp, eatEscapeUp;
    internal Predicate<nint>? TestWindowFilter { private get; init; }
    internal Rectangle? PanelBounds => panel?.Bounds;
    internal DesktopSwitcher(RainPreferences preferences)
    {
        this.preferences = preferences;
        callback = Keyboard;
        foregroundCallback = (_,_,h,_,_,_,_) =>
        {
            if (h==0) return;
            recent.Remove(h);
            recent.Insert(0,h);
            if(recent.Count>256)recent.RemoveAt(256);
        };
        timer.Tick += (_, _) => Tick();
    }

    internal bool Enable(bool value)
    {
        if (!value) { End(false); if (hook != 0) UnhookWindowsHookEx(hook); hook = 0; eatTabUp = eatEscapeUp = false; }
        else if (hook == 0) hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (hook!=0 && foregroundHook==0)
        {
            foregroundHook=SetWinEventHook(3,3,0,foregroundCallback,0,0,0);
            try
            {
                var type=Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
                if(type!=null)desktops=(IVirtualDesktopManager?)Activator.CreateInstance(type);
            }
            catch(COMException) { desktops=null; }
        }
        if (!value)
        {
            if(foregroundHook!=0)UnhookWinEvent(foregroundHook);
            foregroundHook=0;
            recent.Clear();
            if(desktops!=null)Marshal.ReleaseComObject(desktops);
            desktops=null;
        }
        return !value || hook != 0;
    }

    private bool Eligible(nint window)
    {
        if (TestWindowFilter!=null && !TestWindowFilter(window)) return false;
        if (!Native.IsWindowVisible(window) || window == Native.GetShellWindow() || GetWindowTextLengthW(window) == 0) return false;
        int style = Native.GetWindowLong(window, -20);
        if ((style & 0x08000080) != 0 || (GetWindow(window, 4) != 0 && (style & 0x40000) == 0)) return false;
        if(desktops!=null && desktops.IsWindowOnCurrentVirtualDesktop(window,out int current)==0 && current==0)return false;
        return Native.GetCloaked(window, 14, out int cloaked, 4) != 0 || cloaked == 0;
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            int key = Marshal.ReadInt32(data);
            bool down = message == 0x100 || message == 0x104;
            if (key == 9 && !down && eatTabUp) { eatTabUp = false; return 1; }
            if (key == 27 && !down && eatEscapeUp) { eatEscapeUp = false; return 1; }
            if (active && !down && key is 18 or 164 or 165) End(true);
            if (active && key == 27 && down) { eatEscapeUp = true; End(false); return 1; }
            if (down && key == 9 && (Marshal.ReadInt32(data, 8) & 32) != 0 && GetAsyncKeyState(17) >= 0)
            {
                if (!active)
                {
                    origin = Native.GetForegroundWindow();
                    windows.Clear();
                    Native.EnumWindows((h, _) => { if (Eligible(h)) windows.Add(h); return true; }, 0);
                    if (windows.Count < 2) return CallNextHookEx(0, code, message, data);
                    foreach(var handle in recent.AsEnumerable().Reverse())
                        if(windows.Remove(handle))windows.Insert(0,handle);
                    if(windows.Remove(origin)) windows.Insert(0,origin);
                    selected = windows.IndexOf(origin);
                    if (selected < 0) selected = GetAsyncKeyState(16) < 0 ? 0 : -1;
                    started = lastRefresh = Environment.TickCount64;
                    active = true;
                    timer.Start();
                }
                selected = (selected + windows.Count + (GetAsyncKeyState(16) < 0 ? -1 : 1)) % windows.Count;
                eatTabUp = true;
                return 1;
            }
        }
        return CallNextHookEx(0, code, message, data);
    }

    private void Tick()
    {
        if (!active || Environment.TickCount64 - started < 120) return;
        if (Environment.TickCount64 - lastRefresh >= 120)
        {
            nint current = windows[selected];
            windows.RemoveAll(h => !Eligible(h));
            if (windows.Count == 0) { End(false); return; }
            selected = windows.Contains(current) ? windows.IndexOf(current) : Math.Min(selected, windows.Count - 1);
            lastRefresh = Environment.TickCount64;
        }
        panel ??= new SwitchCards(Forms.Screen.FromHandle(origin), preferences.Night);
        panel.UpdateCards(windows, selected);
    }

    private void End(bool commit)
    {
        timer.Stop();
        nint target = active && commit ? windows[selected] : 0;
        active = false;
        panel?.Dispose();
        panel = null;
        if (target != 0 && Eligible(target))
        {
            if (Native.IsIconic(target)) ShowWindowAsync(target, 9);
            SetForegroundWindow(target);
        }
    }

    public void Dispose() { Enable(false); timer.Dispose(); }
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint min,uint max,nint module,ForegroundProc callback,uint process,uint thread,uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, KeyboardProc proc, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint cmd);
    [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
}

internal sealed class SwitchCards : Forms.Form
{
    private readonly Rectangle area;
    private readonly float scale;
    private readonly Color paper, ink, accent;
    private readonly List<(nint window, nint thumbnail, string title, Icon? icon)> cards = new();
    private int selection = -1;
    private float petal;
    private int Unit(float value) => Math.Max(1, (int)Math.Round(value * scale));
    private int Cell => Math.Min(Unit(184), area.Width);
    protected override bool ShowWithoutActivation => true;
    protected override Forms.CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x08000080; return p; }
    }

    internal SwitchCards(Forms.Screen screen, bool night)
    {
        area = screen.WorkingArea;
        StartPosition = Forms.FormStartPosition.Manual;
        FormBorderStyle = Forms.FormBorderStyle.None;
        Location = area.Location;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        scale = DirectRain.GetDpiForWindow(Handle) / 96f;
        paper = night ? Color.FromArgb(41,27,41) : Color.FromArgb(251,240,225);
        ink = night ? Color.MistyRose : Color.FromArgb(64,40,48);
        accent = night ? Color.Plum : Color.FromArgb(161,74,97);
        BackColor = paper;
    }

    internal void UpdateCards(List<nint> windows, int selected)
    {
        int capacity = Math.Clamp((area.Width - Unit(32)) / Cell, 1, 6);
        int first = selected / capacity * capacity;
        int count = Math.Min(capacity, windows.Count - first);
        bool rebuild = cards.Count != count;
        for (int i = 0; !rebuild && i < count; i++) rebuild = cards[i].window != windows[first+i];
        if (rebuild)
        {
            ClearCards();
            Size = new Size(Cell * count, Unit(148));
            Location = new Point(area.Left + (area.Width - Width)/2, area.Top + (area.Height - Height)/2);
            if (!Visible) Show();
            for (int i = 0; i < count; i++)
            {
                nint window = windows[first+i], thumb = 0;
                var name = new StringBuilder(512);
                GetWindowTextW(window, name, name.Capacity);
                var box = new Rectangle(i*Cell+Unit(10),Unit(10),Cell-Unit(20),Unit(94));
                if (DwmRegisterThumbnail(Handle, window, out thumb) == 0)
                {
                    if (DwmQueryThumbnailSourceSize(thumb, out var size) == 0 && size.Width > 0 && size.Height > 0)
                    {
                        double fit = Math.Min((double)box.Width/size.Width,(double)box.Height/size.Height);
                        int w=(int)(size.Width*fit), h=(int)(size.Height*fit);
                        box = new Rectangle(box.X+(box.Width-w)/2,box.Y+(box.Height-h)/2,w,h);
                    }
                    var props = new Thumbnail { Flags=9, Rect=new Native.RECT {Left=box.Left,Top=box.Top,Right=box.Right,Bottom=box.Bottom}, Visible=1 };
                    if (DwmUpdateThumbnailProperties(thumb, ref props) != 0) { DwmUnregisterThumbnail(thumb); thumb=0; }
                }
                Icon? icon = null;
                if (thumb == 0)
                {
                    nint raw = Environment.Is64BitProcess ? GetClassLongPtrW(window,-14) : (nint)GetClassLongW(window,-14);
                    if (raw != 0) icon = (Icon)Icon.FromHandle(raw).Clone();
                }
                cards.Add((window,thumb,name.ToString(),icon));
            }
        }
        int next = selected-first;
        float goal = (next+.5f)*Cell;
        if (selection < 0) petal = goal;
        bool moving = Math.Abs(petal-goal) > .5f;
        petal = moving ? petal+(goal-petal)*.34f : goal;
        if (rebuild || selection != next || moving) Invalidate();
        selection = next;
    }

    protected override void OnPaint(Forms.PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(accent,Unit(1));
        using var fill = new SolidBrush(accent);
        for (int i=0;i<cards.Count;i++)
        {
            int left=i*Cell;
            if(i==selection)e.Graphics.DrawRectangle(pen,left+Unit(3),Unit(3),Cell-Unit(6),Height-Unit(6));
            if(cards[i].thumbnail==0)e.Graphics.DrawIcon(cards[i].icon ?? SystemIcons.Application,new Rectangle(left+Cell/2-Unit(16),Unit(40),Unit(32),Unit(32)));
            Forms.TextRenderer.DrawText(e.Graphics,cards[i].title,Font,new Rectangle(left+Unit(8),Unit(107),Cell-Unit(16),Unit(23)),ink,Forms.TextFormatFlags.HorizontalCenter|Forms.TextFormatFlags.EndEllipsis|Forms.TextFormatFlags.SingleLine|Forms.TextFormatFlags.NoPrefix);
        }
        e.Graphics.TranslateTransform(petal,Unit(135));
        e.Graphics.RotateTransform(-24);
        e.Graphics.FillEllipse(fill,-Unit(5),-Unit(2),Unit(11),Unit(5));
    }
    private void ClearCards()
    {
        foreach(var card in cards) { if(card.thumbnail!=0)DwmUnregisterThumbnail(card.thumbnail); card.icon?.Dispose(); }
        cards.Clear();
    }
    protected override void Dispose(bool disposing) { if(disposing)ClearCards(); base.Dispose(disposing); }
    [StructLayout(LayoutKind.Sequential)] private struct Thumbnail { public uint Flags; public Native.RECT Rect,Source; public byte Opacity; public int Visible,ClientOnly; }
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(nint h,StringBuilder s,int n);
    [DllImport("user32.dll")] private static extern nint GetClassLongPtrW(nint h,int i);
    [DllImport("user32.dll")] private static extern uint GetClassLongW(nint h,int i);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination,nint source,out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail,out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail,ref Thumbnail props);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
}
