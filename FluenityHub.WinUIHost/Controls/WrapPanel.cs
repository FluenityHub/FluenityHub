using System;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Controls;

public partial class WrapPanel : Panel
{
    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnSpacingChanged));

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapPanel), new PropertyMetadata(0.0, OnSpacingChanged));

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WrapPanel panel)
        {
            panel.InvalidateMeasure();
            panel.InvalidateArrange();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;

        double curLineW = 0;
        double curLineH = 0;
        double totalW = 0;
        double totalH = 0;

        foreach (UIElement child in Children)
        {
            child.Measure(availableSize);
            Size sz = child.DesiredSize;

            if (curLineW > 0 && curLineW + hSpacing + sz.Width > availableSize.Width)
            {
                totalW = Math.Max(totalW, curLineW);
                totalH += curLineH + vSpacing;
                curLineW = sz.Width;
                curLineH = sz.Height;
            }
            else
            {
                curLineW += (curLineW > 0 ? hSpacing : 0) + sz.Width;
                curLineH = Math.Max(curLineH, sz.Height);
            }
        }

        totalW = Math.Max(totalW, curLineW);
        totalH += curLineH;

        return new Size(totalW, totalH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double hSpacing = HorizontalSpacing;
        double vSpacing = VerticalSpacing;

        double x = 0;
        double y = 0;
        double curLineH = 0;

        foreach (UIElement child in Children)
        {
            Size sz = child.DesiredSize;

            if (x > 0 && x + hSpacing + sz.Width > finalSize.Width)
            {
                x = 0;
                y += curLineH + vSpacing;
                curLineH = 0;
            }

            double nextX = x + (x > 0 ? hSpacing : 0);
            child.Arrange(new Rect(nextX, y, sz.Width, sz.Height));

            x = nextX + sz.Width;
            curLineH = Math.Max(curLineH, sz.Height);
        }

        return finalSize;
    }
}
