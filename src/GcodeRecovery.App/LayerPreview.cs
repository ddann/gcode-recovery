using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using GcodeRecovery.Core.Analysis;
using GcodeRecovery.Core.Gcode;

namespace GcodeRecovery.App;

/// <summary>
/// Top-down view of one layer: extrusion paths, the area that is solid in every candidate layer (green
/// tint), the chosen touch-down rectangle and the touch point. Clicking picks a new touch point.
/// </summary>
public sealed class LayerPreview : Control
{
    private static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(0x1e, 0x22, 0x28));
    private static readonly IPen PathPen = new Pen(new SolidColorBrush(Color.FromRgb(0xd8, 0x8a, 0x3a)), 1);
    private static readonly IPen RectPen = new Pen(Brushes.DeepSkyBlue, 2);
    private static readonly IPen SafePointPen = new Pen(Brushes.LimeGreen, 2);
    private static readonly IPen UnsafePointPen = new Pen(Brushes.Red, 2);
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.FromArgb(0xc0, 0xff, 0xff, 0xff));

    private LayerInfo? _layer;
    private OccupancyGrid? _grid;
    private WriteableBitmap? _gridBitmap;
    private RectMm? _touchRect;
    private (double X, double Y)? _touchPoint;
    private bool _touchSafe = true;
    private RectMm _view = new(0, 0, 256, 256);

    public event EventHandler<Point>? TouchPointPicked;

    public LayerPreview()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public void Show(LayerInfo? layer, OccupancyGrid? grid, RectMm? touchRect, (double X, double Y)? touchPoint, bool touchSafe)
    {
        _layer = layer;
        if (!ReferenceEquals(grid, _grid))
        {
            _grid = grid;
            _gridBitmap?.Dispose();
            _gridBitmap = grid is null ? null : BuildBitmap(grid);
        }
        _touchRect = touchRect;
        _touchPoint = touchPoint;
        _touchSafe = touchSafe;
        _view = grid is not null
            ? new RectMm(grid.OriginX, grid.OriginY, grid.OriginX + grid.Cols * grid.CellSize, grid.OriginY + grid.Rows * grid.CellSize)
            : layer is { Segments.Count: > 0 } ? SegmentBounds(layer) : new RectMm(0, 0, 256, 256);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));
        if (_layer is null)
        {
            DrawText(context, "Open a G-code file and press Analyze", new Point(16, 16));
            return;
        }

        if (_gridBitmap is not null && _grid is not null)
        {
            var tl = ToScreen(_grid.OriginX, _grid.OriginY + _grid.Rows * _grid.CellSize);
            var br = ToScreen(_grid.OriginX + _grid.Cols * _grid.CellSize, _grid.OriginY);
            using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                context.DrawImage(_gridBitmap, new Rect(tl, br));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            foreach (var s in _layer.Segments)
            {
                g.BeginFigure(ToScreen(s.X1, s.Y1), false);
                g.LineTo(ToScreen(s.X2, s.Y2));
                g.EndFigure(false);
            }
        }
        context.DrawGeometry(null, PathPen, geometry);

        if (_touchRect is { } r)
        {
            var a = ToScreen(r.X0, r.Y1);
            var b = ToScreen(r.X1, r.Y0);
            context.DrawRectangle(null, RectPen, new Rect(a, b));
        }

        if (_touchPoint is { } p)
        {
            var c = ToScreen(p.X, p.Y);
            var pen = _touchSafe ? SafePointPen : UnsafePointPen;
            context.DrawEllipse(null, pen, c, 8, 8);
            context.DrawLine(pen, new Point(c.X - 14, c.Y), new Point(c.X + 14, c.Y));
            context.DrawLine(pen, new Point(c.X, c.Y - 14), new Point(c.X, c.Y + 14));
            DrawText(context, $"X {p.X:0.0}  Y {p.Y:0.0}", new Point(c.X + 12, c.Y + 10));
        }

        DrawText(context, $"Layer {_layer.Index + 1}  ·  Z {_layer.Z:0.00} mm  ·  view {_view.Width:0} × {_view.Height:0} mm", new Point(12, Bounds.Height - 24));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_layer is null) return;
        var pos = e.GetPosition(this);
        var (scale, ox, oy) = Transform();
        var x = (pos.X - ox) / scale + _view.X0;
        var y = _view.Y1 - (pos.Y - oy) / scale;
        TouchPointPicked?.Invoke(this, new Point(x, y));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    private (double Scale, double OffsetX, double OffsetY) Transform()
    {
        const double pad = 24;
        var w = Math.Max(1, Bounds.Width - 2 * pad);
        var h = Math.Max(1, Bounds.Height - 2 * pad - 20);
        var scale = Math.Min(w / Math.Max(1e-3, _view.Width), h / Math.Max(1e-3, _view.Height));
        var ox = pad + (w - _view.Width * scale) / 2;
        var oy = pad + (h - _view.Height * scale) / 2;
        return (scale, ox, oy);
    }

    private Point ToScreen(double x, double y)
    {
        var (scale, ox, oy) = Transform();
        return new Point(ox + (x - _view.X0) * scale, oy + (_view.Y1 - y) * scale);
    }

    private static void DrawText(DrawingContext context, string text, Point at)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 12, TextBrush);
        context.DrawText(ft, at);
    }

    private static WriteableBitmap BuildBitmap(OccupancyGrid grid)
    {
        var bmp = new WriteableBitmap(new PixelSize(grid.Cols, grid.Rows), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        var row = new int[grid.Cols];
        const int filled = unchecked((int)0x5528a745); // premultiplied translucent green
        for (var r = 0; r < grid.Rows; r++)
        {
            for (var c = 0; c < grid.Cols; c++) row[c] = grid[c, grid.Rows - 1 - r] ? filled : 0;
            System.Runtime.InteropServices.Marshal.Copy(row, 0, fb.Address + r * fb.RowBytes, grid.Cols);
        }
        return bmp;
    }

    private static RectMm SegmentBounds(LayerInfo layer)
    {
        var minX = layer.Segments.Min(s => Math.Min(s.X1, s.X2));
        var maxX = layer.Segments.Max(s => Math.Max(s.X1, s.X2));
        var minY = layer.Segments.Min(s => Math.Min(s.Y1, s.Y2));
        var maxY = layer.Segments.Max(s => Math.Max(s.Y1, s.Y2));
        return new RectMm(minX - 2, minY - 2, maxX + 2, maxY + 2);
    }
}
