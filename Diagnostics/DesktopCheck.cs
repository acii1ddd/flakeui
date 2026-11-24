using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Forms=System.Windows.Forms;

internal static class DesktopCheck
{
    private static readonly string DirectoryPath=Path.Combine(AppContext.BaseDirectory,"desktop-review");
    private static void Log(string message)=>File.AppendAllText(Path.Combine(DirectoryPath,"results.txt"),message+Environment.NewLine);
    internal static int Run()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(Path.Combine(DirectoryPath,"results.txt"),"");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        app.Dispatcher.InvokeAsync(async()=>
        {
            try {await Check();app.Shutdown(0);}
            catch(Exception e){Log(e.ToString());app.Shutdown(1);}
        });
        return app.Run();
    }
    private static void Require(bool pass,string message)
    {
        if(!pass)throw new InvalidOperationException(message);
        Log(message+" OK");
    }
    private static async Task Check()
    {
        var screen=Forms.Screen.PrimaryScreen!;
        Log("Displays: "+Forms.Screen.AllScreens.Length);
        var leftScreen = new Rectangle(-1920,-200,1920,1080);
        foreach(var bar in new[]{new Rectangle(-1920,820,1920,60),new Rectangle(-1920,-200,1920,60),new Rectangle(-1920,-200,60,1080),new Rectangle(-60,-200,60,1080)})
        {
            PondController.Anchor(leftScreen,bar,144,out float x,out float y);
            var scene = Rectangle.FromLTRB(leftScreen.Left+(int)((x-80)*1.5),leftScreen.Top+(int)((y-31)*1.5),leftScreen.Left+(int)((x+80)*1.5),leftScreen.Top+(int)((y+10)*1.5));
            Require(leftScreen.Contains(scene) && !bar.IntersectsWith(scene),"Secondary display placement "+bar);
        }
        using var a=Fixture("Сакура",Color.FromArgb(124,64,91));
        using var b=Fixture("Ночной сад",Color.FromArgb(54,64,96));
        nint firstHandle=a.Handle, secondHandle=b.Handle;
        using var sw=new DesktopSwitcher(new RainPreferences{Night=true}){TestWindowFilter=h=>h==firstHandle||h==secondHandle};
        a.Show();b.Show();
        Require(sw.Enable(true),"Keyboard hook");
        a.Activate();await Task.Delay(150);
        b.Activate();await Task.Delay(150);
        Require(Native.GetForegroundWindow()==b.Handle,"Test window foreground");
        var gif=new GifBitmapEncoder();
        try
        {
            Key(18,true);Key(9,true);Key(9,false);Key(18,false);
            await Task.Delay(180);
            Require(Native.GetForegroundWindow()==a.Handle,"Quick Alt+Tab");
            b.WindowState=Forms.FormWindowState.Minimized;
            Key(18,true);Key(9,true);Key(9,false);
            await Task.Delay(260);
            Require(sw.PanelBounds.HasValue,"Hold shows cards");
            for(int i=0;i<14;i++)
            {
                if(i==5 || i==9){Key(16,true);Key(9,true);Key(9,false);Key(16,false);}
                await Task.Delay(70);
                Capture(gif,sw.PanelBounds!.Value);
            }
            Save(gif,"switcher.gif");
            Key(18,false);
            await Task.Delay(300);
            Require(Native.GetForegroundWindow()==b.Handle && b.WindowState!=Forms.FormWindowState.Minimized,"Restore selected window");
            Key(18,true);Key(9,true);Key(9,false);await Task.Delay(180);
            Key(27,true);Key(27,false);Key(18,false);await Task.Delay(120);
            Require(sw.PanelBounds==null && Native.GetForegroundWindow()==b.Handle,"Escape cancellation");
            Key(18,true);Key(9,true);Key(9,false);await Task.Delay(180);
            a.Close();await Task.Delay(240);Key(18,false);
            Require(sw.PanelBounds==null,"Closing a candidate during selection");
        }
        finally {Key(9,false);Key(16,false);Key(18,false);sw.Enable(false);}
        using var session=new NativeRainSession(e=>Log(e.ToString()),NativeEffect.Rain,screen.Bounds);
        using var controller=new PondController(new RainPreferences(),_=>session);
        await Task.Delay(450);
        async Task Sample(string name,Action? start=null)
        {
            session.TryStartSample(out var frames);
            start?.Invoke();
            await Task.Delay(2000);
            session.StopSample();
            Log(name+" "+frames.Summarize());
        }
        await Sample("Rain / closed");
        using(var cards=new SwitchCards(screen,true))
        {
            cards.UpdateCards(new(){b.Handle},0);
            await Sample("Rain / cards");
        }
        await Sample("Rain / pond",controller.Preview);
        Require(controller.VisiblePond is {Alpha:>0.9f},"Preview on actual taskbar");
        var water=controller.VisiblePond!;
        float scale=session.Dpi/96f;
        var crop=new Rectangle(screen.Bounds.X+(int)((water.X-83)*scale),screen.Bounds.Y+(int)((water.Y-34)*scale),(int)(166*scale),(int)(48*scale));
        gif=new GifBitmapEncoder();
        for(int i=0;i<23;i++){Capture(gif,crop);await Task.Delay(80);}
        Save(gif,"pond.gif");
        Log("Observed rain crossings: "+water.Hits);
        await Task.Delay(1600);
        Require(controller.VisiblePond==null,"Preview expiration");
        controller.Preview();controller.CancelPreview();await Task.Delay(400);
        Require(controller.VisiblePond==null,"Preview cancellation");
        Log("Waiting for an actual idle interval (up to 75 seconds)");
        var pref=new RainPreferences{NightPondEnabled=true};
        using var idle=new PondController(pref,_=>session);
        for(int i=0;i<150 && idle.VisiblePond==null;i++)await Task.Delay(500);
        Require(idle.VisiblePond!=null,"Actual 60-second idle");
        Key(16,true);Key(16,false);await Task.Delay(500);
        Require(idle.VisiblePond==null,"Fade after input");
        pref.NightPondEnabled=false;
        idle.Configure();
        using var standalone=new PondController(new RainPreferences(),_=>null);
        b.FormBorderStyle=Forms.FormBorderStyle.None;
        b.Bounds=screen.Bounds;
        b.Activate();
        standalone.Preview();await Task.Delay(350);
        Require(standalone.VisiblePond==null,"Fullscreen suppression");
        b.Bounds=new Rectangle(screen.WorkingArea.Right-340,screen.WorkingArea.Bottom-140,340,140);
        standalone.Preview();await Task.Delay(350);
        Require(standalone.VisiblePond is {Alpha:>0.9f},"Pond with all other effects off");
        var point=new Native.POINT {X=crop.Left+crop.Width/2,Y=crop.Bottom-10};
        var hit=Native.WindowFromPoint(point);
        Require(hit==b.Handle || IsChild(b.Handle,hit),"Input hit-test through the pond");
        standalone.CancelPreview();
    }

    private static Forms.Form Fixture(string title,Color color)
    {
        var form=new Forms.Form{Text=title,BackColor=color,Size=new System.Drawing.Size(400,260)};
        form.Controls.Add(new Forms.Label{Text=title,Dock=Forms.DockStyle.Fill,TextAlign=ContentAlignment.MiddleCenter,ForeColor=Color.White,Font=new Font("Georgia",20)});
        return form;
    }
    private static void Capture(GifBitmapEncoder encoder,Rectangle rect)
    {
        using var bitmap=new Bitmap(rect.Width,rect.Height);
        using(var graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(rect.Location,System.Drawing.Point.Empty,rect.Size);
        nint handle=bitmap.GetHbitmap();
        try
        {
            var source=Imaging.CreateBitmapSourceFromHBitmap(handle,0,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());
            var metadata=new BitmapMetadata("gif");
            metadata.SetQuery("/grctlext/Delay",(ushort)8);
            encoder.Frames.Add(BitmapFrame.Create(source,null,metadata,null));
        }
        finally {DeleteObject(handle);}
    }
    private static void Save(GifBitmapEncoder encoder,string name)
    {
        using var file=File.Create(Path.Combine(DirectoryPath,name));
        encoder.Save(file);
    }
    private static void Key(byte key,bool down)=>keybd_event(key,0,down?0u:2u,0);
    [DllImport("user32.dll")]private static extern void keybd_event(byte key,byte scan,uint flags,nuint extra);
    [DllImport("user32.dll")]private static extern bool IsChild(nint parent,nint child);
    [DllImport("gdi32.dll")]private static extern bool DeleteObject(nint h);
}
