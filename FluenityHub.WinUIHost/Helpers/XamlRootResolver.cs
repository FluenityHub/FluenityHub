using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluenityHub_WinUIHost.Helpers;

public static class XamlRootResolver
{
    public static XamlRoot? Resolve(FrameworkElement? element)
    {
        if (element?.XamlRoot is not null)
        {
            return element.XamlRoot;
        }

        if (element is Page page && page.Content?.XamlRoot is not null)
        {
            return page.Content.XamlRoot;
        }

        if (MainWindow.Instance?.Content?.XamlRoot is not null)
        {
            return MainWindow.Instance.Content.XamlRoot;
        }

        return null;
    }

    public static async Task<XamlRoot?> ResolveAsync(FrameworkElement? element, int timeoutMs = 2000)
    {
        var immediate = Resolve(element);
        if (immediate is not null)
        {
            return immediate;
        }

        if (element is null)
        {
            return Resolve(null);
        }

        var tcs = new TaskCompletionSource<XamlRoot?>();
        RoutedEventHandler? loadedHandler = null;
        loadedHandler = (s, e) =>
        {
            element.Loaded -= loadedHandler;
            tcs.TrySetResult(Resolve(element));
        };
        element.Loaded += loadedHandler;

        var delayTask = Task.Delay(timeoutMs);
        var finished = await Task.WhenAny(tcs.Task, delayTask);
        if (finished == tcs.Task)
        {
            return await tcs.Task;
        }

        element.Loaded -= loadedHandler;
        return Resolve(element);
    }
}
