using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using GcodeRecovery.Core.Preview;

namespace GcodeRecovery.App;

/// <summary>
/// Lightweight 3D toolpath viewer (orthographic, drawn with Avalonia's 2D API so it runs everywhere
/// without a GPU dependency). Drag to orbit, scroll to zoom, right-drag to pan.
/// Shows the already printed part as a grey ghost and the generated program in colour, up to the
/// move selected by <see cref="VisibleMoves"/> — i.e. exactly what the printer is about to do.
/// </summary>
public sealed class Toolpath3DView : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1a, 0x1f));
    private static readonly IPen GhostPen = new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xb0, 0xb8, 0xc0)), 1);
    private static readonly IPen TravelPen = new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0x4f, 0xa3, 0xff)), 1);
    private static readonly IPen ExtrudePen = new Pen(new SolidColorBrush(Color.FromRgb(0xff, 0x8c, 0x1a)), 1.4);
    private static readonly IPen ProbePen = new Pen(Brushes.Red, 3);
    private static readonly IPen AxisX = new Pen(Brushes.IndianRed, 1.5);
    private static readonly IPen AxisY = new Pen(Brushes.LimeGreen, 1.5);
    private static readonly IPen AxisZ = new Pen(Brushes.DodgerBlue, 1.5);
    private static readonly IBrush NozzleBrush = Brushes.White;
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(0xd0, 0xff, 0xff, 0xff));

    private IReadOnlyList<Move3D> _ghost = [];
    private IReadOnlyList<Move3D> _program = [];
    private double _yaw = -0.7, _pitch = 0.95, _zoom = 1;
    private Vector _pan;
    private Point? _drag;
    private bool _panning;
    private (float X, float Y, float Z) _center;
    private float _extent = 100;
    private int _visible;

    public Toolpath3DView()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    /// <summary>Number of program moves drawn (scrubber). Values ≥ move count show everything.</summary>
    public int VisibleMoves
    {
        get => _visible;
        set
        {
            _visible = Math.Clamp(value, 0, _program.Count);
            InvalidateVisual();
        }
    }

    public int MoveCount => _program.Count;

    /// <summary>Live correction shown on the nozzle marker (where the nozzle really goes).</summary>
    public (double X, double Y, double Z) NozzleOffset { get; set; }

    /// <summary>Index of the first move whose source line is at or after <paramref name="line"/>.</summary>
    public int MoveIndexForLine(int line)
    {
        int lo = 0, hi = _program.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (_program[mid].Line < line) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
    public Move3D? CurrentMove => _visible > 0 && _visible <= _program.Count ? _program[_visible - 1] : null;

    public void SetScene(IReadOnlyList<Move3D> ghost, IReadOnlyList<Move3D> program)
    {
        _ghost = ghost;
        _program = program;
        _visible = program.Count;
        FitToContent();
        InvalidateVisual();
    }

    public void ResetView()
    {
        _yaw = -0.7;
        _pitch = 0.95;
        _zoom = 1;
        _pan = default;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_program.Count == 0 && _ghost.Count == 0)
        {
            Text(context, "Generate a recovery program to see the 3D preview", new Point(16, 16));
            return;
        }

        DrawAxes(context);
        DrawBatch(context, _ghost, _ghost.Count, GhostPen, null);
        DrawBatch(context, _program, _visible, TravelPen, MoveKind.Travel);
        DrawBatch(context, _program, _visible, ExtrudePen, MoveKind.Extrude);
        DrawBatch(context, _program, _visible, ProbePen, MoveKind.Probe);

        if (CurrentMove is { } m)
        {
            var (ox, oy, oz) = NozzleOffset;
            var p = Project(m.X2, m.Y2, m.Z2);
            var corrected = Project((float)(m.X2 + ox), (float)(m.Y2 + oy), (float)(m.Z2 + oz));
            if (ox != 0 || oy != 0 || oz != 0)
            {
                context.DrawEllipse(null, new Pen(NozzleBrush, 1), p, 4, 4);
                context.DrawLine(new Pen(Brushes.Yellow, 1.5), p, corrected);
                context.DrawEllipse(Brushes.Yellow, null, corrected, 5, 5);
            }
            else
            {
                context.DrawEllipse(NozzleBrush, null, p, 4, 4);
            }
            Text(context, $"nozzle  X {m.X2 + ox:0.00}  Y {m.Y2 + oy:0.00}  Z {m.Z2 + oz:0.00}   ({m.Kind})" +
                          (ox != 0 || oy != 0 || oz != 0 ? $"   offset {ox:+0.00;-0.00} / {oy:+0.00;-0.00} / {oz:+0.00;-0.00}" : ""),
                new Point(12, Bounds.Height - 22));
        }
        Text(context, "drag: orbit · right-drag: pan · wheel: zoom", new Point(12, 10));
    }

    private void DrawBatch(DrawingContext context, IReadOnlyList<Move3D> moves, int count, IPen pen, MoveKind? kind)
    {
        // Very large programs are decimated so orbiting stays interactive.
        var step = Math.Max(1, count / 250_000);
        var geometry = new StreamGeometry();
        var any = false;
        using (var g = geometry.Open())
        {
            for (var i = 0; i < count; i += step)
            {
                var m = moves[i];
                if (kind is { } k && m.Kind != k) continue;
                g.BeginFigure(Project(m.X1, m.Y1, m.Z1), false);
                g.LineTo(Project(m.X2, m.Y2, m.Z2));
                g.EndFigure(false);
                any = true;
            }
        }
        if (any) context.DrawGeometry(null, pen, geometry);
    }

    private void DrawAxes(DrawingContext context)
    {
        var o = Project(_center.X - _extent / 2, _center.Y - _extent / 2, 0);
        var len = _extent / 5;
        context.DrawLine(AxisX, o, Project(_center.X - _extent / 2 + len, _center.Y - _extent / 2, 0));
        context.DrawLine(AxisY, o, Project(_center.X - _extent / 2, _center.Y - _extent / 2 + len, 0));
        context.DrawLine(AxisZ, o, Project(_center.X - _extent / 2, _center.Y - _extent / 2, len));
    }

    private Point Project(float x, float y, float z)
    {
        double dx = x - _center.X, dy = y - _center.Y, dz = z - _center.Z;
        var cy = Math.Cos(_yaw);
        var sy = Math.Sin(_yaw);
        // Yaw spins around the vertical axis; pitch 0 = side view, π/2 = top view.
        var screenX = dx * cy - dy * sy;
        var depth = dx * sy + dy * cy;
        var screenY = -dz * Math.Cos(_pitch) - depth * Math.Sin(_pitch);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / _extent * 0.8 * _zoom;
        return new Point(Bounds.Width / 2 + screenX * scale + _pan.X, Bounds.Height / 2 + screenY * scale + _pan.Y);
    }

    private void FitToContent()
    {
        float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
        float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
        foreach (var m in _ghost.Concat(_program.Where(p => p.Kind != MoveKind.Travel)))
        {
            minX = Math.Min(minX, Math.Min(m.X1, m.X2)); maxX = Math.Max(maxX, Math.Max(m.X1, m.X2));
            minY = Math.Min(minY, Math.Min(m.Y1, m.Y2)); maxY = Math.Max(maxY, Math.Max(m.Y1, m.Y2));
            minZ = Math.Min(minZ, Math.Min(m.Z1, m.Z2)); maxZ = Math.Max(maxZ, Math.Max(m.Z1, m.Z2));
        }
        if (minX > maxX) return;
        _center = ((minX + maxX) / 2, (minY + maxY) / 2, (minZ + maxZ) / 2);
        _extent = Math.Max(10, Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        _drag = e.GetPosition(this);
        _panning = e.GetCurrentPoint(this).Properties.IsRightButtonPressed;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_drag is not { } start) return;
        var p = e.GetPosition(this);
        var d = p - start;
        _drag = p;
        if (_panning) _pan += d;
        else
        {
            _yaw += d.X * 0.01;
            _pitch = Math.Clamp(_pitch + d.Y * 0.01, 0.05, Math.PI / 2);
        }
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _drag = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        _zoom = Math.Clamp(_zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), 0.1, 50);
        InvalidateVisual();
    }

    private static void Text(DrawingContext context, string text, Point at) =>
        context.DrawText(new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, TextBrush), at);
}
