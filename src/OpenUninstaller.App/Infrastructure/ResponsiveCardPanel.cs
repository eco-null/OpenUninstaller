using System.Windows;
using System.Windows.Controls;

namespace OpenUninstaller.App.Infrastructure;

/// <summary>Equal-width cards placed in the next available column without stretching short forms.</summary>
public sealed class ResponsiveCardPanel : Panel
{
    public static readonly DependencyProperty MinCardWidthProperty = DependencyProperty.Register(
        nameof(MinCardWidth), typeof(double), typeof(ResponsiveCardPanel),
        new FrameworkPropertyMetadata(480d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double width && double.IsFinite(width) && width > 0);

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(ResponsiveCardPanel),
        new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double gap && double.IsFinite(gap) && gap >= 0);

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveCardPanel),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int count && count > 0);

    public double MinCardWidth
    {
        get => (double)GetValue(MinCardWidthProperty);
        set => SetValue(MinCardWidthProperty, value);
    }

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    private int Columns(double width) => double.IsFinite(width)
        ? Math.Min(MaxColumns, Math.Max(1, (int)Math.Floor((Math.Max(0, width) + Gap) / (MinCardWidth + Gap)))) : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : MinCardWidth;
        var columns = Columns(width);
        var cardWidth = Math.Max(0, (width - (columns - 1) * Gap) / columns);
        var heights = new double[columns];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(cardWidth, double.PositiveInfinity));
            heights[NextColumn(heights)] += child.DesiredSize.Height + Gap;
        }
        return new Size(width, Math.Max(0, heights.Max() - Gap));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cardWidth = Math.Max(0, (finalSize.Width - (columns - 1) * Gap) / columns);
        var heights = new double[columns];
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var column = NextColumn(heights);
            child.Arrange(new Rect(column * (cardWidth + Gap), heights[column], cardWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height + Gap;
        }
        return finalSize;
    }

    private static int NextColumn(double[] heights)
    {
        var column = 0;
        for (var index = 1; index < heights.Length; index++)
            if (heights[index] < heights[column]) column = index;
        return column;
    }
}
