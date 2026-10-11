using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace mirr.Controls;

public sealed class LetterboxPanel : Panel
{
    public static readonly DependencyProperty SourceWidthProperty = DependencyProperty.Register(
        nameof(SourceWidth),
        typeof(double),
        typeof(LetterboxPanel),
        new FrameworkPropertyMetadata(390.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SourceHeightProperty = DependencyProperty.Register(
        nameof(SourceHeight),
        typeof(double),
        typeof(LetterboxPanel),
        new FrameworkPropertyMetadata(844.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty RotationProperty = DependencyProperty.Register(
        nameof(Rotation),
        typeof(double),
        typeof(LetterboxPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double SourceWidth
    {
        get => (double)GetValue(SourceWidthProperty);
        set => SetValue(SourceWidthProperty, value);
    }

    public double SourceHeight
    {
        get => (double)GetValue(SourceHeightProperty);
        set => SetValue(SourceHeightProperty, value);
    }

    public double Rotation
    {
        get => (double)GetValue(RotationProperty);
        set => SetValue(RotationProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double sourceWidth = Math.Max(1, SourceWidth);
        double sourceHeight = Math.Max(1, SourceHeight);
        (double boxWidth, double boxHeight) = RotatedBox(sourceWidth, sourceHeight);

        double availableWidth = Normalized(availableSize.Width, boxWidth);
        double availableHeight = Normalized(availableSize.Height, boxHeight);
        double scale = Math.Min(availableWidth / boxWidth, availableHeight / boxHeight);

        double childWidth = sourceWidth * scale;
        double childHeight = sourceHeight * scale;
        UIElement? child = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        child?.Measure(new Size(childWidth, childHeight));

        return new Size(boxWidth * scale, boxHeight * scale);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double sourceWidth = Math.Max(1, SourceWidth);
        double sourceHeight = Math.Max(1, SourceHeight);
        (double boxWidth, double boxHeight) = RotatedBox(sourceWidth, sourceHeight);

        double scale = Math.Min(finalSize.Width / boxWidth, finalSize.Height / boxHeight);
        double childWidth = sourceWidth * scale;
        double childHeight = sourceHeight * scale;

        UIElement? child = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        if (child is null)
        {
            return finalSize;
        }

        double offsetX = (finalSize.Width - childWidth) / 2;
        double offsetY = (finalSize.Height - childHeight) / 2;
        child.Arrange(new Rect(offsetX, offsetY, childWidth, childHeight));

        if (child.RenderTransform is RotateTransform transform)
        {
            transform.CenterX = childWidth / 2;
            transform.CenterY = childHeight / 2;
            transform.Angle = Rotation;
        }
        else
        {
            child.RenderTransform = new RotateTransform(Rotation, childWidth / 2, childHeight / 2);
        }

        double radius = Math.Min(16, childWidth * 0.04);
        child.Clip = new RectangleGeometry(
            new Rect(0, 0, childWidth, childHeight),
            radius,
            radius);

        return finalSize;
    }

    private (double Width, double Height) RotatedBox(double width, double height)
    {
        double rotation = Rotation % 360;
        bool quarter = Math.Abs(rotation - 90) < 0.01 || Math.Abs(rotation - 270) < 0.01;
        return quarter ? (height, width) : (width, height);
    }

    private static double Normalized(double value, double fallback) =>
        double.IsInfinity(value) || double.IsNaN(value) || value <= 0 ? fallback : value;
}
