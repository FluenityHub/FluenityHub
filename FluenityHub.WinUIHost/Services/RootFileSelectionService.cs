using System;
using System.Collections.Generic;

namespace FluenityHub_WinUIHost.Services;

internal static class RootFileSelectionService
{
    internal static bool TryAdd(ICollection<string> selectedFiles, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(selectedFiles);

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        foreach (var selectedFile in selectedFiles)
        {
            if (string.Equals(selectedFile, fileName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        selectedFiles.Add(fileName);
        return true;
    }
}
