using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ADBControl.Desktop.Views;

public sealed class GridBackground : Canvas
{
    public GridBackground()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => DrawGrid();
    }

    public double GridSize { get; set; } = 24;

    public Brush? GridLineBrush { get; set; }

    public Brush? Fill { get; set; }

    public void Refresh()
    {
        DrawGrid();
    }

    private void DrawGrid()
    {
        Children.Clear();
        Background = Fill;
        if (GridLineBrush is null || GridSize <= 0)
            return;

        for (double x = 0; x <= ActualWidth; x += GridSize)
            Children.Add(Line(new Point(x, 0), new Point(x, ActualHeight)));

        for (double y = 0; y <= ActualHeight; y += GridSize)
            Children.Add(Line(new Point(0, y), new Point(ActualWidth, y)));
    }

    private Microsoft.UI.Xaml.Shapes.Line Line(Point from, Point to)
    {
        return new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = from.X,
            Y1 = from.Y,
            X2 = to.X,
            Y2 = to.Y,
            Stroke = GridLineBrush,
            StrokeThickness = 1,
        };
    }
}
