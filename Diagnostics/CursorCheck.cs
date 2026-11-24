using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlakeUI;

internal static class CursorCheck
{
    [DllImport("user32")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32")] private static extern void mouse_event(uint flags,uint x,uint y,uint data,nuint extra);
    private static void Click(ButtonBase b) => b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Require(bool value,string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(SettingsWindow panel, Action<NativeEffect> select, Func<NativeRainSession?> current,
        SettingsStore store, string config, string directory)
    {
        Directory.CreateDirectory(directory);
        Click(panel.CursorTab);
        Require(panel.Rain.IsChecked == false,"Cursor should initially be disabled");
        Require(store.Values.Style==CursorStyle.Petals,"Default style for existing settings");
        panel.StylePicker.SelectedIndex=(int)CursorStyle.Ink;
        Require(panel.Rain.IsChecked==false && store.Values.Style==CursorStyle.Ink,"Style changed enabled state");
        panel.Speed.Value=50; panel.Density.Value=0;
        await Task.Delay(550);
        using(var saved=new SettingsStore(config))
            Require(saved.Values.Style==CursorStyle.Ink && saved.Values.TrailLength==50 && saved.Values.ClickForce==0,"Cursor persistence");
        panel.Speed.Value=150; panel.Density.Value=150;
        Click(panel.ResetButton);
        Require(store.Values.Style==CursorStyle.Ink && store.Values.ClickForce==100 && store.Values.TrailLength==100,"Cursor reset");
        Click(panel.Rain);
        Require(current()?.Effect==(NativeEffect.Cursor|NativeEffect.Runner),"Independent cursor toggle");

        var probe=new PetalSwarm();
        for(int i=0;i<120;i++) probe.Step(new Point(-1,50),1.0/60,1.5,100);
        int visible=0;bool left=false,right=false;
        foreach(var p in probe.Petals)
        {
            if(p.Alpha<.02) continue;
            visible++;
            left |= p.Position.X<0;
            right |= p.Position.X>=0;
        }
        Require(visible==3 && left && right,"Idle swarm crossing the zero boundary");
        var velocity=probe.Petals[0].Velocity;
        probe.Click(new Point(-1,50),false,0,1.5);
        Require(probe.Petals[0].Velocity==velocity,"Zero impulse");

        panel.Topmost=true; panel.Activate();
        await Task.Delay(350);
        var hwnd=new WindowInteropHelper(panel).Handle;
        var demo=panel.PetalDemo;
        void Move(Point point)
        {
            var pixel=demo.PointToScreen(point);
            var p=new Native.POINT{X=(int)pixel.X,Y=(int)pixel.Y};
            Require(Native.WindowFromPoint(p)==hwnd,"Preview is covered");
            SetCursorPos(p.X,p.Y);
        }
        Native.GetCursorPos(out var original);
        var log=new StringBuilder();
        try
        {
            foreach(var tab in new[]{panel.RainTab,panel.RunnerTab,panel.SnowTab,panel.CursorTab})
            {
                Click(tab);
                panel.Activate(); await Task.Delay(250);
                foreach(var slider in new[]{panel.Speed,panel.Density})
                {
                    slider.Value=slider.Maximum*.5;
                    panel.UpdateLayout();
                    var track=(Track)slider.Template.FindName("PART_Track",slider);
                    async Task PointAt(double fraction)
                    {
                        var pos=track.PointToScreen(new Point(track.Thumb.ActualWidth*.5+
                            fraction*(track.ActualWidth-track.Thumb.ActualWidth),track.ActualHeight*.5));
                        var pt=new Native.POINT{X=(int)Math.Round(pos.X),Y=(int)Math.Round(pos.Y)};
                        var hit=Native.WindowFromPoint(pt);
                        if(hit!=hwnd)
                        {
                            Native.GetWindowRect(hwnd,out var rect);
                            throw new InvalidOperationException($"Test slider covered {tab.Content}/{slider.Name} at {pt.X},{pt.Y}, width={track.ActualWidth}, expected={hwnd} ({rect.Left},{rect.Top},{rect.Right},{rect.Bottom}), actual={hit}");
                        }
                        SetCursorPos(pt.X,pt.Y);
                        await Task.Delay(65);
                    }
                    await PointAt(.19);
                    try
                    {
                        mouse_event(2,0,0,0,0);await Task.Delay(70);
                        Require(Math.Abs(slider.Value-slider.Maximum*.19)<1.2,"Track press");
                        foreach(double f in new[]{.5,1.08,.74})
                        {
                            await PointAt(f);
                            Require(Math.Abs(slider.Value-slider.Maximum*Math.Min(1,f))<1.2,"Track drag: "+tab.Content+"/"+slider.Name+" "+f+" got "+slider.Value);
                        }
                    }
                    finally {mouse_event(4,0,0,0,0);await Task.Delay(70);}
                    await PointAt(.30);
                    mouse_event(2,0,0,0,0);mouse_event(4,0,0,0,0);await Task.Delay(70);
                    Require(Math.Abs(slider.Value-slider.Maximum*.3)<1.2,"Single track click");
                    try
                    {
                        mouse_event(2,0,0,0,0);await Task.Delay(70);await PointAt(.61);
                        Require(Math.Abs(slider.Value-slider.Maximum*.61)<1.2,"Thumb drag");
                    }
                    finally {mouse_event(4,0,0,0,0);await Task.Delay(70);}
                    slider.Value=100;
                }
            }
            foreach(var style in Enum.GetValues<CursorStyle>())
            {
            panel.StylePicker.SelectedIndex=(int)style;
            // Each scenario uses the same path and number of input samples.
            foreach(var effects in new[]{NativeEffect.Cursor,NativeEffect.Snow|NativeEffect.Runner|NativeEffect.Cursor})
            {
                select(effects);
                // For 'none' measure the empty DirectX surface as the isolated cursor baseline.
                using var empty=effects==NativeEffect.None ? new NativeRainSession(_=>{},NativeEffect.None) : null;
                var session=current()??empty!;
                await Task.Delay(300);
                Require(session.TryStartSample(out var frames),"Sample busy");
                for(int i=0;i<150;i++)
                {
                    Move(new Point(127+90*Math.Sin(i*.065),36+12*Math.Cos(i*.09)));
                    await Task.Delay(20);
                }
                session.StopSample();
                log.AppendLine($"{style} / {effects}: {frames.Summarize()}");
            }
            var cursor=current()!.Cursor!;
            int before=cursor.Impulses;
            var gif=new GifBitmapEncoder();
            for(int i=0;i<90;i++)
            {
                double x=i<18?40+i*9:i<32?202-(i-18)*7:104;
                Move(new Point(x,37));
                if(i==48) mouse_event(2,0,0,0,0);
                if(i==53) mouse_event(4,0,0,0,0);
                if(i==65) mouse_event(8,0,0,0,0);
                if(i==68) mouse_event(16,0,0,0,0);
                await Task.Delay(50);
                var bitmap=new RenderTargetBitmap(440,350,96,96,PixelFormats.Pbgra32);
                bitmap.Render(panel);
                var meta=new BitmapMetadata("gif");
                meta.SetQuery("/grctlext/Delay",(ushort)6);
                gif.Frames.Add(BitmapFrame.Create(bitmap,null,meta,null));
            }
            Require(cursor.Impulses==before+2,"Expected one raw impulse per press");
            using(var file=File.Create(Path.Combine(directory,style.ToString().ToLowerInvariant()+".gif"))) gif.Save(file);
            }
            File.WriteAllText(Path.Combine(directory,"cursor-metrics.txt"),log.ToString());
        }
        finally { SetCursorPos(original.X,original.Y); }
    }
}