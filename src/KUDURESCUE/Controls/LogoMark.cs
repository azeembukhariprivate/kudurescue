using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Kudurescue.Controls
{
    /// <summary>The KUDURESCUE mark: a lifebuoy around a file. Uses the text colour, with the stripes see-through.</summary>
    public class LogoMark : Viewbox
    {
        public LogoMark(double size = 40)
        {
            Width = size;
            Height = size;
            Focusable = false;
            IsHitTestVisible = false;

            var canvas = new Canvas { Width = 64, Height = 64 };

            // The ring: four solid quarters with gaps, like the stripes on a lifebuoy.
            var ring = new Ellipse { Width = 52, Height = 52, StrokeThickness = 9, StrokeDashCap = PenLineCap.Flat };
            var segment = System.Math.PI * 43 / 8 / 9; // eighths of the ring's centre line, in stroke widths
            ring.StrokeDashArray = new DoubleCollection { segment, segment };
            ring.RenderTransformOrigin = new Point(0.5, 0.5);
            ring.RenderTransform = new RotateTransform(21.5);
            Canvas.SetLeft(ring, 6);
            Canvas.SetTop(ring, 6);
            Bind(ring, Shape.StrokeProperty);
            canvas.Children.Add(ring);

            var outline = new Ellipse { Width = 52, Height = 52, StrokeThickness = 1.6 };
            Canvas.SetLeft(outline, 6);
            Canvas.SetTop(outline, 6);
            Bind(outline, Shape.StrokeProperty);
            canvas.Children.Add(outline);

            var inner = new Ellipse { Width = 34, Height = 34, StrokeThickness = 1.6 };
            Canvas.SetLeft(inner, 15);
            Canvas.SetTop(inner, 15);
            Bind(inner, Shape.StrokeProperty);
            canvas.Children.Add(inner);

            var file = new Path { Data = Geometry.Parse("M26,22 H34.5 L40,27.5 V42 H26 Z") };
            Bind(file, Shape.FillProperty);
            canvas.Children.Add(file);

            Child = canvas;
        }

        private void Bind(Shape shape, DependencyProperty property) =>
            shape.SetBinding(property, new Binding { Path = new PropertyPath(TextElement.ForegroundProperty), Source = this });
    }
}
