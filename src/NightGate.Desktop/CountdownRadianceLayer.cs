using System.Windows;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace NightGate.Desktop;

/// <summary>A small retained visual, drawn behind the opaque countdown card.</summary>
public sealed class CountdownRadianceLayer : FrameworkElement
{
    private readonly Dictionary<byte, SolidColorBrush> _brushes = [];
    private readonly Dictionary<(byte Alpha, double Thickness), Pen> _pens = [];
    private CountdownRadianceLayout? _layout;
    private CountdownRadianceFrame? _frame;
    private Color _color = Colors.SeaGreen;
    private bool _highContrast;

    internal CountdownRadianceFrame? CurrentFrame => _frame;

    internal event EventHandler? RenderingFailed;

    internal void Clear()
    {
        _frame = null;
        InvalidateVisual();
    }

    internal void Apply(
        CountdownRadianceLayout layout,
        CountdownRadianceFrame frame,
        Color color,
        bool highContrast)
    {
        if (_color != color)
        {
            _brushes.Clear();
            _pens.Clear();
            _color = color;
        }

        _layout = layout;
        _frame = frame;
        _highContrast = highContrast;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        try
        {
            RenderRadiance(drawingContext);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            _frame = null;
            try
            {
                RenderingFailed?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception notificationException) when (IsRecoverable(notificationException))
            {
                // A decorative layer must never fail the WPF rendering loop.
            }
        }
    }

    private void RenderRadiance(DrawingContext drawingContext)
    {
        if (_layout is not { } layout || _frame is not { } frame)
        {
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(new Rect(RenderSize)));
        try
        {
        if (!_highContrast)
        {
            // Several narrow translucent strokes provide a bounded halo without
            // a blur surface, shader, or continuously allocated bitmap.
            for (int index = 5; index >= 1; index--)
            {
                DrawRing(drawingContext, layout, index * 2.4,
                    frame.GlowOpacity * (6 - index) / 6, 2.8);
            }
        }

        foreach (var wave in frame.Waves)
        {
            DrawRing(drawingContext, layout, wave.Inflation,
                _highContrast ? 0.9 : wave.Opacity,
                _highContrast ? Math.Max(1.5, wave.Thickness) : wave.Thickness);
        }

        if (!_highContrast)
        {
            foreach (var particle in frame.Particles)
            {
                Point tip = new(particle.X, particle.Y);
                Point tail = new(particle.TrailX, particle.TrailY);
                Point midpoint = new((tip.X + tail.X) / 2, (tip.Y + tail.Y) / 2);
                drawingContext.DrawLine(PenFor(particle.Opacity * 0.2, 1), tail, midpoint);
                drawingContext.DrawLine(PenFor(particle.Opacity * 0.5, 1.2), midpoint, tip);
                if (particle.Shape == CountdownParticleShape.Diamond)
                {
                    StreamGeometry diamond = new();
                    using (StreamGeometryContext geometry = diamond.Open())
                    {
                        geometry.BeginFigure(new(tip.X, tip.Y - particle.Radius), true, true);
                        geometry.LineTo(new(tip.X + particle.Radius, tip.Y), true, false);
                        geometry.LineTo(new(tip.X, tip.Y + particle.Radius), true, false);
                        geometry.LineTo(new(tip.X - particle.Radius, tip.Y), true, false);
                    }

                    diamond.Freeze();
                    drawingContext.DrawGeometry(BrushFor(particle.Opacity), null, diamond);
                }
                else if (particle.Shape == CountdownParticleShape.Streak)
                {
                    drawingContext.DrawLine(PenFor(particle.Opacity, Math.Max(1.2, particle.Radius)), midpoint, tip);
                }
                else
                {
                    drawingContext.DrawEllipse(BrushFor(particle.Opacity), null, tip, particle.Radius, particle.Radius);
                }
            }
        }

        }
        finally
        {
            drawingContext.Pop();
        }
    }

    private void DrawRing(DrawingContext context, CountdownRadianceLayout layout,
        double inflation, double opacity, double thickness)
    {
        if (opacity <= 0 || thickness <= 0)
        {
            return;
        }

        Rect ring = new(layout.Halo - inflation, layout.Halo - inflation,
            layout.CardWidth + inflation * 2, layout.CardHeight + inflation * 2);
        context.DrawRoundedRectangle(null, PenFor(opacity, thickness), ring,
            14 + inflation, 14 + inflation);
    }

    private SolidColorBrush BrushFor(double opacity)
    {
        byte alpha = AlphaFor(opacity);
        if (!_brushes.TryGetValue(alpha, out SolidColorBrush? brush))
        {
            brush = new(Color.FromArgb(alpha, _color.R, _color.G, _color.B));
            brush.Freeze();
            _brushes.Add(alpha, brush);
        }

        return brush;
    }

    private Pen PenFor(double opacity, double thickness)
    {
        byte alpha = AlphaFor(opacity);
        double width = Math.Clamp(Math.Round(thickness * 2) / 2, 0.5, 5);
        if (!_pens.TryGetValue((alpha, width), out Pen? pen))
        {
            pen = new(BrushFor(opacity), width)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            pen.Freeze();
            _pens.Add((alpha, width), pen);
        }

        return pen;
    }

    private byte AlphaFor(double opacity) =>
        (byte)Math.Clamp(Math.Round(Math.Clamp(opacity, 0, 1) * _color.A / 8) * 8, 0, 255);

    private static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException);
}
