#region

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

#endregion

namespace DemoViewer.NET.Visualization.Internal;

internal sealed class PanZoomHandler
{
    private Point? _dragStart;
    internal double PanX { get; private set; }

    internal double PanY { get; private set; }

    internal double Zoom { get; private set; } = 1.0;

    internal bool OnPointerMoved(Control control, PointerEventArgs e)
    {
        if (_dragStart is { } start && Equals(e.Pointer.Captured, control))
        {
            Point pos = e.GetPosition(control);
            PanX += pos.X - start.X;
            PanY += pos.Y - start.Y;
            _dragStart = pos;
            return true;
        }

        return false;
    }

    internal bool OnPointerPressed(Control control, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(control).Properties.IsLeftButtonPressed)
        {
            _dragStart = e.GetPosition(control);
            e.Pointer.Capture(control);
            return true;
        }

        return false;
    }

    internal bool OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragStart is not null)
        {
            _dragStart = null;
            e.Pointer.Capture(null);
            return true;
        }

        return false;
    }

    internal bool OnPointerWheelChanged(Control control, PointerWheelEventArgs e)
    {
        double factor = e.Delta.Y > 0 ? 1.15 : 1 / 1.15;
        double newZoom = Math.Clamp(Zoom * factor, 0.15, 5.0);
        if (Math.Abs(newZoom - Zoom) < 1e-10)
        {
            return false;
        }

        Point pos = e.GetPosition(control);
        PanX = pos.X - (pos.X - PanX) * (newZoom / Zoom);
        PanY = pos.Y - (pos.Y - PanY) * (newZoom / Zoom);
        Zoom = newZoom;
        return true;
    }

    /// <summary>Restores the identity view: zoom=1, pan=(0,0), and cancels any drag.</summary>
    internal void Reset()
    {
        Zoom = 1.0;
        PanX = 0;
        PanY = 0;
        _dragStart = null;
    }
}
