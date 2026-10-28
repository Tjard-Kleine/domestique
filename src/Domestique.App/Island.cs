// Open with a spring (response 0.5, damping 0.72) and close with the 340 ms curve as in Coucou
// (https://github.com/Louis-CFM/coucou, windows/src/island/island.ts), MIT License, see THIRD-PARTY-NOTICES.md.
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Domestique.App;

// Shape of the overlay while opening, closing, minimizing and docking. The panel keeps its layout; only a clipped
// part, centered at the top, is visible. When the spring overshoots the full size the panel is scaled slightly
// so the bounce shows. The notch ears stay attached to the edges. Runs only while moving; at rest there is no clip.
internal sealed class Island
{
    private readonly Border _shell;
    private readonly UIElement _leftEar, _rightEar;
    private readonly Tracked _w = new(0), _h = new(0), _r = new(0);
    private readonly FrameLoop _loop;
    private Action? _done;
    private bool _opening;                                                    // opening to full size

    public Island(Border shell, UIElement leftEar, UIElement rightEar)
    {
        _shell = shell;
        _leftEar = leftEar;
        _rightEar = rightEar;
        _loop = new FrameLoop((dt, now) =>
        {
            if (_opening) { _w.Retarget(FullWidth); _h.Retarget(FullHeight); }
            _w.Step(dt, now); _h.Step(dt, now); _r.Step(dt, now);
            Apply();
            if (Animating) return true;
            Release(force: _opening);
            _opening = false;
            var done = _done;
            _done = null;
            done?.Invoke();
            return false;
        });
    }

    public bool Docked { get; set; }
    public bool Animating => _w.Animating || _h.Animating || _r.Animating;
    // Visible part of the panel in its own coordinates.
    public Rect VisibleRect
    {
        get
        {
            double fw = FullWidth, fh = FullHeight;
            if (_shell.Clip is null) return new Rect(0, 0, fw, fh);
            double cw = Math.Min(_w.Value, fw), ch = Math.Min(_h.Value, fh);
            return new Rect((fw - cw) / 2, 0, Math.Max(0, cw), Math.Max(0, ch));
        }
    }

    public double FullWidth => _shell.ActualWidth;
    public double FullHeight => _shell.ActualHeight;
    private double FullRadius => _shell.CornerRadius.BottomLeft;

    // Jumps to this size (e.g. the small pill before opening).
    public void Jump(double w, double h, double r)
    {
        _w.Jump(w); _h.Jump(h); _r.Jump(r);
        Apply();
    }

    // Opens with a spring to the full panel size.
    public void Open(Action? done = null, double response = 0.5, double damping = 0.72)
    {
        _shell.UpdateLayout();
        _w.SpringTo(FullWidth, response, damping);
        _h.SpringTo(FullHeight, response, damping);
        _r.SpringTo(FullRadius, response, damping);
        _opening = true;
        Run(done);
    }

    // Closes without overshoot (Coucou: 340 ms, cubic-bezier(.45, 0, .2, 1)).
    public void Close(double w, double h, double r, Action? done = null, double ms = 340)
    {
        if (!Animating && _shell.Clip is null) { _w.Jump(FullWidth); _h.Jump(FullHeight); _r.Jump(FullRadius); }
        _opening = false;
        double now = _loop.Now;
        _w.CurveTo(w, ms, now); _h.CurveTo(h, ms, now); _r.CurveTo(r, ms, now);
        Run(done);
    }

    private void Run(Action? done)
    {
        _done = done;
        if (!Motion.Enabled)                                                 // animations turned off in Windows: jump to the end
        {
            _w.Jump(_w.TargetValue); _h.Jump(_h.TargetValue); _r.Jump(_r.TargetValue);
            Release(force: _opening);
            _opening = false;
            _done = null;
            done?.Invoke();
            return;
        }
        _loop.Start();
    }

    private void Apply()
    {
        double fw = FullWidth, fh = FullHeight;
        if (fw <= 0 || fh <= 0) return;
        double w = Math.Max(0, _w.Value), h = Math.Max(0, _h.Value), r = Math.Max(0, Math.Min(_r.Value, Math.Min(w, h) / 2));
        var scale = Motion.Scale(_shell);
        _shell.RenderTransformOrigin = new Point(0.5, 0);
        scale.ScaleX = Math.Max(1, w / fw);
        scale.ScaleY = Math.Max(1, h / fh);
        double cw = Math.Min(w, fw), ch = Math.Min(h, fh);
        _shell.Clip = Shape(new Rect((fw - cw) / 2, 0, cw, ch), Docked ? 0 : r, r);
        double edge = (fw - w) / 2;                                          // ears to the edges
        Motion.Shift(_leftEar).X = edge;
        Motion.Shift(_rightEar).X = -edge;
        double ear = Math.Clamp(h / 14, 0, 1);
        Motion.Scale(_leftEar).ScaleY = Motion.Scale(_rightEar).ScaleY = ear;
    }

    // After a layout change (e.g. minimized): full size at once, no clip.
    public void Reset()
    {
        _loop.Stop();
        _done = null;
        _opening = false;
        _w.Jump(FullWidth); _h.Jump(FullHeight); _r.Jump(FullRadius);
        Release(force: true);
    }

    // At rest: no clip, no scale.
    private void Release(bool force = false)
    {
        if (!force && (Math.Abs(_w.Value - FullWidth) > 0.5 || Math.Abs(_h.Value - FullHeight) > 0.5)) return;
        _shell.Clip = null;
        var scale = Motion.Scale(_shell);
        scale.ScaleX = scale.ScaleY = 1;
        Motion.Shift(_leftEar).X = Motion.Shift(_rightEar).X = 0;
        Motion.Scale(_leftEar).ScaleY = Motion.Scale(_rightEar).ScaleY = 1;
    }

    // Rectangle with separate top and bottom radii (docked: square top).
    private static Geometry Shape(Rect r, double top, double bottom)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left + top, r.Top), true, true);
            c.LineTo(new Point(r.Right - top, r.Top), false, false);
            if (top > 0) c.ArcTo(new Point(r.Right, r.Top + top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right, r.Bottom - bottom), false, false);
            if (bottom > 0) c.ArcTo(new Point(r.Right - bottom, r.Bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Left + bottom, r.Bottom), false, false);
            if (bottom > 0) c.ArcTo(new Point(r.Left, r.Bottom - bottom), new Size(bottom, bottom), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Left, r.Top + top), false, false);
            if (top > 0) c.ArcTo(new Point(r.Left + top, r.Top), new Size(top, top), 0, false, SweepDirection.Clockwise, false, false);
        }
        g.Freeze();
        return g;
    }
}
