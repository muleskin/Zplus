using Microsoft.Maui.Graphics;
using ZPlus.Shared.Dtos;

namespace ZPlus.Mobile;

/// <summary>
/// Renders whiteboard strokes on a GraphicsView. Strokes store normalized 0..1 points,
/// so they scale to whatever size the canvas happens to be.
/// </summary>
public class WhiteboardDrawable : IDrawable
{
    public List<WhiteboardStrokeDto> Strokes { get; } = [];

    public void Draw(ICanvas canvas, RectF rect)
    {
        canvas.FillColor = Color.FromArgb("#FF14161A");
        canvas.FillRectangle(rect);

        canvas.StrokeLineJoin = LineJoin.Round;
        canvas.StrokeLineCap = LineCap.Round;

        foreach (var s in Strokes)
        {
            if (s.Points.Count < 4) continue;
            canvas.StrokeColor = ParseColor(s.Color);
            canvas.StrokeSize = (float)s.Width;
            var path = new PathF();
            path.MoveTo((float)(s.Points[0] * rect.Width), (float)(s.Points[1] * rect.Height));
            for (int i = 2; i + 1 < s.Points.Count; i += 2)
                path.LineTo((float)(s.Points[i] * rect.Width), (float)(s.Points[i + 1] * rect.Height));
            canvas.DrawPath(path);
        }
    }

    private static Color ParseColor(string hex)
    {
        try { return Color.FromArgb(hex); } catch { return Colors.White; }
    }
}
