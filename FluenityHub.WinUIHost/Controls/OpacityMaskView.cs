// Adapted from the MIT-licensed Microsoft WinUI Gallery OpacityMaskView.
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace FluenityHub_WinUIHost.Controls;

[TemplatePart(Name = RootGridTemplateName, Type = typeof(Grid))]
[TemplatePart(Name = MaskContainerTemplateName, Type = typeof(Border))]
[TemplatePart(Name = ContentPresenterTemplateName, Type = typeof(ContentPresenter))]
public sealed partial class OpacityMaskView : ContentControl
{
    public static readonly DependencyProperty OpacityMaskProperty =
        DependencyProperty.Register(
            nameof(OpacityMask),
            typeof(UIElement),
            typeof(OpacityMaskView),
            new PropertyMetadata(null, OnOpacityMaskChanged));

    private const string ContentPresenterTemplateName = "PART_ContentPresenter";
    private const string MaskContainerTemplateName = "PART_MaskContainer";
    private const string RootGridTemplateName = "PART_RootGrid";

    private readonly Compositor _compositor =
        CompositionTarget.GetCompositorForCurrentThread();
    private CompositionBrush? _mask;
    private CompositionMaskBrush? _maskBrush;

    public OpacityMaskView()
    {
        DefaultStyleKey = typeof(OpacityMaskView);
    }

    public UIElement? OpacityMask
    {
        get => (UIElement?)GetValue(OpacityMaskProperty);
        set => SetValue(OpacityMaskProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild(RootGridTemplateName) is not Grid rootGrid ||
            GetTemplateChild(ContentPresenterTemplateName) is not ContentPresenter contentPresenter ||
            GetTemplateChild(MaskContainerTemplateName) is not Border maskContainer)
        {
            return;
        }

        _maskBrush = _compositor.CreateMaskBrush();
        _maskBrush.Source = GetVisualBrush(contentPresenter);
        _mask = GetVisualBrush(maskContainer);
        _maskBrush.Mask = OpacityMask is null ? null : _mask;

        var redirectVisual = _compositor.CreateSpriteVisual();
        redirectVisual.RelativeSizeAdjustment = Vector2.One;
        redirectVisual.Brush = _maskBrush;
        ElementCompositionPreview.SetElementChildVisual(rootGrid, redirectVisual);
    }

    private static CompositionBrush GetVisualBrush(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var visualSurface = compositor.CreateVisualSurface();
        visualSurface.SourceVisual = visual;

        var sourceSizeAnimation =
            compositor.CreateExpressionAnimation($"{nameof(visual)}.Size");
        sourceSizeAnimation.SetReferenceParameter(nameof(visual), visual);
        visualSurface.StartAnimation(
            nameof(visualSurface.SourceSize),
            sourceSizeAnimation);

        var brush = compositor.CreateSurfaceBrush(visualSurface);
        visual.Opacity = 0;
        return brush;
    }

    private static void OnOpacityMaskChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        var view = (OpacityMaskView)sender;
        if (view._maskBrush is not null)
        {
            view._maskBrush.Mask = args.NewValue is null ? null : view._mask;
        }
    }
}
