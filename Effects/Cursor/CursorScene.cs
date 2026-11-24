using System;
using System.Collections.Generic;
using System.Windows;

internal enum CursorStyle { Petals, Ink, Fireflies }
internal enum Tint { Ink, Blossom, Lantern, Light }
internal readonly record struct Mark(Point A, Point B, double Size, double Alpha, Tint Tint, bool Dot);

internal sealed class CursorScene
{
    internal PetalSwarm Petals { get; private set; } = new();
    internal CursorStyle Style { get; private set; }
    internal readonly List<Mark> Marks = new(256);
    private readonly (Point P,double Time)[] history = new (Point,double)[80];
    private readonly (Point P,Vector Dir,double Born,double Gain)[] branches = new (Point,Vector,double,double)[8];
    private readonly (Point P,Vector Velocity,double Born)[] fragments = new (Point,Vector,double)[32];
    private readonly Point[] lights = new Point[8];
    private readonly double[] arrivals = new double[8];
    private Point last, bloomCenter;
    private Vector direction = new(1,0);
    private double time, quiet, nextBranch, swirlStart, bloomStart, pulseStart, bloomGain, pulseGain;
    private int head,count,branch,fragment;
    private bool initialized;

    internal void Select(CursorStyle style)
    {
        if (Style == style) return;
        Style=style; initialized=false; Marks.Clear();
        if(style==CursorStyle.Petals) Petals=new();
    }

    internal void Step(Point cursor,double dt,double scale,int length)
    {
        if(Style==CursorStyle.Petals){ Petals.Step(cursor,dt,scale,length); return; }
        dt=Math.Clamp(dt,0,.05);
        if(!initialized || (last-cursor).Length>1000*scale)
        {
            initialized=true; last=cursor; time=quiet=nextBranch=0;
            head=count=branch=fragment=0;
            Array.Clear(branches); Array.Clear(fragments); Array.Fill(lights,cursor);
            bloomStart=pulseStart=-10; swirlStart=0;
        }
        time+=dt; Marks.Clear();
        Vector delta=cursor-last;
        bool moving=delta.Length>.35*scale;
        quiet=moving?0:quiet+dt;
        if(Style==CursorStyle.Ink)
        {
            if(moving)
            {
                Vector next=delta/delta.Length;
                if(Vector.Multiply(next,direction)<.7 && time>nextBranch && count>=3)
                    Branch(last,direction-next,.7);
                direction=next; swirlStart=time;
                Push(cursor);
            }
            DrawInk(cursor,scale,length);
        }
        else DrawLights(cursor,dt,scale,length);
        last=cursor;
    }
    private void Push(Point p)
    {
        history[head]=(p,time); head=(head+1)%history.Length;
        count=Math.Min(history.Length,count+1);
    }
    private void Branch(Point p,Vector dir,double gain)
    {
        if(dir.Length<.01) dir=new Vector(.7,-1);
        dir.Normalize();
        branches[branch]=(p,dir,time,gain); branch=(branch+1)%branches.Length;
        nextBranch=time+.13;
    }
    private static Vector Unit(double a)=>new(Math.Cos(a),Math.Sin(a));
    private void Line(Point a,Point b,double width,double opacity,Tint tint)
        => Marks.Add(new(a,b,width,Math.Clamp(opacity,0,1),tint,false));
    private void Dot(Point p,double radius,double opacity,Tint tint)
        => Marks.Add(new(p,p,radius,Math.Clamp(opacity,0,1),tint,true));

    private void DrawInk(Point cursor,double scale,int length)
    {
        double life=.009*length;
        for(int i=1;i<count && life>0;i++)
        {
            var a=history[(head-i+history.Length)%history.Length];
            var b=history[(head-i-1+history.Length)%history.Length];
            double fade=1-(time-b.Time)/life;
            if(fade<=0) break;
            Line(a.P,b.P,scale*(.35+1.7*fade),fade,Tint.Ink);
        }
        if(quiet>.28)
        {
            double cycle=(time-swirlStart-.28)%2.2/2.2;
            Point p=cursor+Unit(time*.28)*12*scale;
            for(int j=1;j<=24;j++)
            {
                double t=cycle*j/24;
                Point q=cursor+Unit(time*.28+t*5.4)*(12+t*10)*scale;
                Line(p,q,1.2*scale,Math.Sin(Math.PI*cycle)*.8,Tint.Ink);p=q;
            }
        }
        foreach(var b in branches)
        {
            double age=time-b.Born;
            if(b.Gain<=0 || age>1.55) continue;
            double grow=Math.Min(1,age/.30), fade=Math.Clamp((1.55-age)/.65,0,1);
            Vector stem=b.Dir*(23*b.Gain*scale*grow);
            Vector side=new(-stem.Y,stem.X);
            Point fork=b.P+stem*.52;
            Point tip=b.P+stem+side*.19;
            Point bud=fork+stem*.28-side*.32;
            Line(b.P,fork,1.7*scale,fade,Tint.Ink);Line(fork,tip,scale,fade,Tint.Ink);
            Line(fork,bud,.85*scale,fade,Tint.Ink);
            Dot(tip,2*scale*grow,fade,Tint.Blossom);Dot(bud,1.55*scale*grow,fade,Tint.Blossom);
        }
        foreach(var f in fragments)
        {
            double age=time-f.Born;
            if(f.Velocity.LengthSquared==0 || age>1)continue;
            Point p=f.P+f.Velocity*(1-Math.Exp(-2*age))*.5+new Vector(0,24*scale*age*age);
            Line(p,p+Unit(age*5+f.Velocity.X)*3*scale,2.3*scale,1-age,Tint.Blossom);
        }
    }
    private void DrawLights(Point cursor,double dt,double scale,int length)
    {
        double bloomAge=time-bloomStart;
        double bloom=bloomAge<1.25?Math.Sin(Math.PI*bloomAge/1.25)*bloomGain:0;
        double flower=quiet>.6?Math.Pow(Math.Max(0,Math.Sin(quiet*1.1)),6):0;
        for(int i=0;i<lights.Length;i++)
        {
            double angle=i*Math.Tau/lights.Length;
            Vector orbit=Unit(time*(.63+.047*i)+i*2.4)*(16+6*Math.Sin(time+i));
            Vector petal=Unit(angle+time*.1)*(i%2==0?27:13);
            Point target=cursor+scale*((1-flower)*orbit+flower*petal);
            if(bloom>0) target+=(bloomCenter+Unit(angle)*scale*(27+14*bloomGain)-target)*Math.Min(1,bloom);
            lights[i]+=(target-lights[i])*(1-Math.Exp(-dt*45/(1+length*.009*i)));
        }
        for(int i=0;i<lights.Length;i++)
        {
            Point p=lights[i];
            for(int j=0;j<i;j++)
            {
                double separation=(p-lights[j]).Length/scale;
                if(separation<22)Line(p,lights[j],.75*scale,.45*(1-separation/22),Tint.Lantern);
            }
            double age=time-pulseStart-arrivals[i];
            double flash=age>=0 && age<.4 ? pulseGain*Math.Sin(Math.PI*age/.4):0;
            double radius=scale*(1.35+.3*Math.Sin(time*3+i)+flash);
            Dot(p,radius*3,.15+.11*flash,Tint.Lantern);Dot(p,radius,.95,Tint.Light);
        }
    }
    internal void Click(Point p,bool right,int strength,double scale)
    {
        if(Style==CursorStyle.Petals){Petals.Click(p,right,strength,scale);return;}
        if(strength==0 || !initialized)return;
        double gain=strength/100.0;
        if(Style==CursorStyle.Ink)
        {
            if(!right){Branch(p,new Vector(direction.X*.65,-1),gain);return;}
            int nearest=-1;double distance=120*scale;
            for(int i=0;i<branches.Length;i++)
            {
                var b=branches[i];double d=(p-b.P).Length;
                if(b.Gain>0 && time-b.Born<1.55 && d<distance){nearest=i;distance=d;}
            }
            if(nearest>=0){p=branches[nearest].P;branches[nearest]=default;}
            else {if(count>0 && quiet<.28)p=history[(head+history.Length-1)%history.Length].P;count=0;swirlStart=time;quiet=0;}
            for(int i=0;i<7;i++)
            {
                fragments[fragment]=(p,Unit(i*2.4+time)*(30+4*i)*gain*scale,time);
                fragment=(fragment+1)%fragments.Length;
            }
        }
        else if(right){bloomStart=time;bloomCenter=p;bloomGain=gain;}
        else
        {
            pulseStart=time;pulseGain=gain;Array.Fill(arrivals,-1);
            for(int order=0;order<lights.Length;order++)
            {
                int best=-1;double d=double.PositiveInfinity;
                for(int i=0;i<lights.Length;i++)
                    if(arrivals[i]<0 && (p-lights[i]).LengthSquared<d){best=i;d=(p-lights[i]).LengthSquared;}
                arrivals[best]=order*.085;p=lights[best];
            }
        }
    }
}