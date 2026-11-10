using System.Runtime.InteropServices;
using FluenityHub_WinUIHost.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluenityHub_WinUIHost.Controls;

public sealed partial class TemplateCardBanner : UserControl
{
    public static readonly DependencyProperty BannerBrushProperty = DependencyProperty.Register(
        nameof(BannerBrush), typeof(Brush), typeof(TemplateCardBanner), new PropertyMetadata(null));
    public static readonly DependencyProperty CoverSourceProperty = DependencyProperty.Register(
        nameof(CoverSource), typeof(ImageSource), typeof(TemplateCardBanner), new PropertyMetadata(null, OnArtworkChanged));
    public static readonly DependencyProperty PreviewUriProperty = DependencyProperty.Register(
        nameof(PreviewUri), typeof(Uri), typeof(TemplateCardBanner), new PropertyMetadata(null, OnArtworkChanged));

    private const int MaxCachedPreviews = 16;
    private static readonly List<(Uri Uri, WeakReference<BitmapImage> Image)> PreviewImages = [];
    private CancellationTokenSource? _previewLoad;

    public Brush? BannerBrush
    {
        get => (Brush?)GetValue(BannerBrushProperty);
        set => SetValue(BannerBrushProperty, value);
    }

    public ImageSource? CoverSource
    {
        get => (ImageSource?)GetValue(CoverSourceProperty);
        set => SetValue(CoverSourceProperty, value);
    }

    public Uri? PreviewUri
    {
        get => (Uri?)GetValue(PreviewUriProperty);
        set => SetValue(PreviewUriProperty, value);
    }

    public TemplateCardBanner()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadArtwork();
        Unloaded += (_, _) =>
        {
            _previewLoad?.Cancel();
            if (PreviewUri is not null) CoverImage.Source = null;
        };
    }

    private static void OnArtworkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is TemplateCardBanner { IsLoaded: true } banner) banner.LoadArtwork();
    }

    private async void LoadArtwork()
    {
        _previewLoad?.Cancel();
        CoverImage.Source = CoverSource;
        if (CoverSource is not null || PreviewUri is not { } uri) return;

        var cachedIndex = PreviewImages.FindIndex(preview => preview.Uri == uri);
        if (cachedIndex >= 0)
        {
            var cached = PreviewImages[cachedIndex];
            PreviewImages.RemoveAt(cachedIndex);
            if (cached.Image.TryGetTarget(out var image))
            {
                PreviewImages.Add(cached);
                CoverImage.Source = image;
                return;
            }
        }

        using var request = new CancellationTokenSource();
        _previewLoad = request;
        try
        {
            using var source = await UnityTemplateCatalogService.OpenPreviewAsync(uri, request.Token);
            if (source is null || request.IsCancellationRequested) return;
            using var stream = source.AsRandomAccessStream();
            var image = new BitmapImage { DecodePixelWidth = 512 };
            await image.SetSourceAsync(stream);
            if (!request.IsCancellationRequested && IsLoaded)
            {
                CoverImage.Source = image;
                PreviewImages.RemoveAll(preview => preview.Uri == uri);
                PreviewImages.Add((uri, new(image)));
                if (PreviewImages.Count > MaxCachedPreviews) PreviewImages.RemoveAt(0);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or COMException or ArgumentException)
        {
            // The template remains usable without its cover.
            System.Diagnostics.Debug.WriteLine($"Template preview unavailable: {ex.GetType().Name}");
        }
        finally
        {
            if (ReferenceEquals(_previewLoad, request)) _previewLoad = null;
        }
    }
}
