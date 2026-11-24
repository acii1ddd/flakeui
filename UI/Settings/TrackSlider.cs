using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace FlakeUI;
internal sealed class TrackSlider : Slider
{
    private Track? dragging;
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var track=Template.FindName("PART_Track",this) as Track;
        if(track==null || track.Thumb.IsMouseOver){base.OnPreviewMouseLeftButtonDown(e);return;}
        Focus();
        if(CaptureMouse()){dragging=track;Update(e);e.Handled=true;}
        else base.OnPreviewMouseLeftButtonDown(e);
    }
    private void Update(MouseEventArgs e)
    {
        var track=dragging!;
        double value=track.ValueFromPoint(e.GetPosition(track));
        if(double.IsFinite(value))SetCurrentValue(ValueProperty,Math.Clamp(value,Minimum,Maximum));
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        if(dragging==null){base.OnMouseMove(e);return;}
        Update(e);e.Handled=true;
    }
    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if(dragging==null){base.OnPreviewMouseLeftButtonUp(e);return;}
        Update(e);ReleaseMouseCapture();e.Handled=true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e){dragging=null;base.OnLostMouseCapture(e);}
}