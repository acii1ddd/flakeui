using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace FlakeUI;
internal sealed class PetalPreview : Canvas
{
    private readonly CursorScene scene = new();
    private static readonly Color[] colors = [Color.FromRgb(132,78,109),Color.FromRgb(245,145,187),Color.FromRgb(231,162,67),Color.FromRgb(255,221,141)];
    private readonly SolidColorBrush[,] brushes = new SolidColorBrush[4,17];
    private readonly Pen[,,] pens = new Pen[4,17,12];
    internal void Select(CursorStyle style)
    {
        scene.Select(style);
        foreach(var petal in petals) petal.Visibility=style==CursorStyle.Petals?Visibility.Visible:Visibility.Hidden;
        InvalidateVisual();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        foreach(var m in scene.Marks)
        {
            int tint=(int)m.Tint, alpha=(int)Math.Round(m.Alpha*16);
            var brush=brushes[tint,alpha];
            if(brush==null){brush=new SolidColorBrush(colors[tint]){Opacity=alpha/16.0};brush.Freeze();brushes[tint,alpha]=brush;}
            if(m.Dot)dc.DrawEllipse(brush,null,m.A,m.Size,m.Size);
            else
            {
                int w=Math.Clamp((int)Math.Round(m.Size*4),1,11);
                var pen=pens[tint,alpha,w];
                if(pen==null){pen=new Pen(brush,w/4.0);pen.Freeze();pens[tint,alpha,w]=pen;}
                dc.DrawLine(pen,m.A,m.B);
            }
        }
    }
    private readonly Path[] petals = new Path[PetalSwarm.Capacity];
    private readonly MatrixTransform[] transforms = new MatrixTransform[PetalSwarm.Capacity];
    private Point target = new(127,36);
    internal int Strength { get; set; } = 100;

    public PetalPreview()
    {
        Width = 254; Height = 74; ClipToBounds = true; Background = Brushes.Transparent;
        var geometry = new StreamGeometry();
        using (var pen = geometry.Open())
        {
            pen.BeginFigure(PetalSwarm.Outline[0], true, true);
            for (int i=1; i<PetalSwarm.Outline.Length; i++) pen.LineTo(PetalSwarm.Outline[i],true,true);
        }
        geometry.Freeze();
        for (int i=0;i<petals.Length;i++)
        {
            transforms[i] = new MatrixTransform();
            var path = petals[i] = new Path { Data=geometry, RenderTransform=transforms[i], IsHitTestVisible=false, Opacity=0 };
            path.SetResourceReference(Shape.FillProperty,"Petal");
            path.SetResourceReference(Shape.StrokeProperty,"Rim");
            path.StrokeThickness=.55;
            Children.Add(path);
        }
        MouseMove += (_,e) => target=e.GetPosition(this);
        MouseDown += (_,e) =>
        {
            if (e.ChangedButton is MouseButton.Left or MouseButton.Right)
                Impulse(e.GetPosition(this), e.ChangedButton == MouseButton.Right);
        };
    }
    internal void Impulse(Point point, bool right) => scene.Click(point,right,Strength,1);
    internal void Step(double dt,int length,Point? position=null)
    {
        target=position ?? target;
        scene.Step(target,dt,1,length);
        if(scene.Style!=CursorStyle.Petals){InvalidateVisual();return;}
        for(int i=0;i<petals.Length;i++)
        {
            ref var p=ref scene.Petals.Petals[i];
            petals[i].Opacity=p.Alpha*p.Visibility;
            if(petals[i].Opacity<.01) continue;
            var matrix=Matrix.Identity;
            matrix.Scale(1,p.Width);
            matrix.Rotate(p.Angle*180/Math.PI);
            matrix.Translate(p.Position.X,p.Position.Y);
            transforms[i].Matrix=matrix;
        }
    }
}