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
    private Grid? _rootGrid;
    private ContentPresenter? _contentPresenter;
    private Border? _maskContainer;
    private CompositionVisualSurface? _sourceSurface;
    private CompositionVisualSurface? _maskSurface;
    private CompositionSurfaceBrush? _sourceBrush;
    private CompositionSurfaceBrush? _mask;
    private CompositionMaskBrush? _maskBrush;
    private SpriteVisual? _redirectVisual;

    public OpacityMaskView()
    {
        DefaultStyleKey = typeof(OpacityMaskView);
        Loaded += (_, _) => CreateComposition();
        Unloaded += (_, _) => ReleaseComposition();
    }

    public UIElement? OpacityMask
    {
        get => (UIElement?)GetValue(OpacityMaskProperty);
        set => SetValue(OpacityMaskProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        ReleaseComposition();
        base.OnApplyTemplate();

        if (GetTemplateChild(RootGridTemplateName) is not Grid rootGrid ||
            GetTemplateChild(ContentPresenterTemplateName) is not ContentPresenter contentPresenter ||
            GetTemplateChild(MaskContainerTemplateName) is not Border maskContainer)
        {
            return;
        }

        _rootGrid = rootGrid;
        _contentPresenter = contentPresenter;
        _maskContainer = maskContainer;
        if (IsLoaded) CreateComposition();
    }

    private void CreateComposition()
    {
        if (_maskBrush is not null || _rootGrid is null || _contentPresenter is null || _maskContainer is null) return;
        _maskBrush = _compositor.CreateMaskBrush();
        _sourceBrush = GetVisualBrush(_contentPresenter, out _sourceSurface);
        _maskBrush.Source = _sourceBrush;
        _mask = GetVisualBrush(_maskContainer, out _maskSurface);
        _maskBrush.Mask = OpacityMask is null ? null : _mask;

        _redirectVisual = _compositor.CreateSpriteVisual();
        _redirectVisual.RelativeSizeAdjustment = Vector2.One;
        _redirectVisual.Brush = _maskBrush;
        ElementCompositionPreview.SetElementChildVisual(_rootGrid, _redirectVisual);
    }

    private void ReleaseComposition()
    {
        if (_rootGrid is not null) ElementCompositionPreview.SetElementChildVisual(_rootGrid, null);
        _sourceSurface?.StopAnimation(nameof(CompositionVisualSurface.SourceSize));
        _maskSurface?.StopAnimation(nameof(CompositionVisualSurface.SourceSize));
        if (_contentPresenter is not null) ElementCompositionPreview.GetElementVisual(_contentPresenter).Opacity = 1;
        if (_maskContainer is not null) ElementCompositionPreview.GetElementVisual(_maskContainer).Opacity = 1;
        if (_redirectVisual is not null) _redirectVisual.Brush = null;
        if (_maskBrush is not null) { _maskBrush.Source = null; _maskBrush.Mask = null; }
        if (_sourceSurface is not null) _sourceSurface.SourceVisual = null;
        if (_maskSurface is not null) _maskSurface.SourceVisual = null;
        _redirectVisual?.Dispose();
        _maskBrush?.Dispose();
        _sourceBrush?.Dispose();
        _mask?.Dispose();
        _sourceSurface?.Dispose();
        _maskSurface?.Dispose();
        _redirectVisual = null;
        _maskBrush = null;
        _sourceBrush = null;
        _mask = null;
        _sourceSurface = null;
        _maskSurface = null;
    }

    private static CompositionSurfaceBrush GetVisualBrush(UIElement element, out CompositionVisualSurface visualSurface)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        visualSurface = compositor.CreateVisualSurface();
        visualSurface.SourceVisual = visual;

        using var sourceSizeAnimation =
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
