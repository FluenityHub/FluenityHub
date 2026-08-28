using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class TemplateService
{
    private static readonly string[] TemplateImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
    private const string ProjectNameToken = "%PROJECT_NAME%";
    private const int MaximumRootFileCount = 50;
    private const long MaximumRootFilesTotalBytes = 10 * 1024 * 1024;
    private const long MaximumPlaceholderFileBytes = 1024 * 1024;
    private readonly string _unityHubTemplatesDir;
    private readonly IReadOnlyList<string> _templateSearchPaths;

    public TemplateService()
    {
        _unityHubTemplatesDir = new UnityHubTemplateSettingsService().GetCurrentPath();
        Directory.CreateDirectory(_unityHubTemplatesDir);

        List<string> customPaths;
        try
        {
            customPaths = new AppSettingsStore().Load().CustomTemplatePaths ?? [];
        }
        catch
        {
            customPaths = [];
        }

        _templateSearchPaths = new[] { _unityHubTemplatesDir }
            .Concat(customPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<CustomTemplateInfo> GetCustomTemplates()
    {
        var result = new Dictionary<string, CustomTemplateInfo>(StringComparer.OrdinalIgnoreCase);

        // Scan Unity Hub's configured location first, then FluenityHub's additional search paths.
        foreach (var searchPath in _templateSearchPaths)
        {
            try
            {
                if (!Directory.Exists(searchPath))
                {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(searchPath))
                {
                    var packageJsonPath = Path.Combine(dir, "package.json");
                    if (!File.Exists(packageJsonPath)) continue;

                    try
                    {
                        var json = File.ReadAllText(packageJsonPath);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string slug = root.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                        string displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString()?.Trim() ?? slug : slug;
                        string version = root.TryGetProperty("version", out var v) ? v.GetString()?.Trim() ?? "1.0.0" : "1.0.0";
                        if (string.IsNullOrWhiteSpace(version)) version = "1.0.0";
                        string unityVersion = root.TryGetProperty("unity", out var u) ? u.GetString()?.Trim() ?? "" : "";
                        string description = root.TryGetProperty("description", out var d) ? d.GetString()?.Trim() ?? "" : "";
                        var includedRootFiles = root.TryGetProperty("rootFiles", out var rootFilesElement) &&
                                                rootFilesElement.ValueKind == JsonValueKind.Array
                            ? rootFilesElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString())
                                .Where(item => !string.IsNullOrWhiteSpace(item))
                                .Select(item => item!)
                                .ToList()
                            : [];
                        var hasProjectNamePlaceholder =
                            root.TryGetProperty("hasProjectNamePlaceholder", out var placeholderElement) &&
                            placeholderElement.ValueKind == JsonValueKind.True;
                        DateTime creationDate = DateTime.Now;
                        if (root.TryGetProperty("creationDate", out var cd) && DateTime.TryParse(cd.GetString(), out var parsedDate))
                        {
                            creationDate = parsedDate;
                        }

                        string tgzPath = Path.Combine(dir, $"{slug}.tgz");
                        if (!File.Exists(tgzPath))
                        {
                            var tgzFiles = Directory.GetFiles(dir, "*.tgz");
                            if (tgzFiles.Length > 0) tgzPath = tgzFiles[0];
                        }

                        var tagsList = new List<string>();
                        if (root.TryGetProperty("keywords", out var kwElement) && kwElement.ValueKind == JsonValueKind.Array)
                        {
                            tagsList.AddRange(kwElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString())
                                .Where(item => !string.IsNullOrWhiteSpace(item))
                                .Select(item => item!.Trim()));
                        }
                        if (root.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array)
                        {
                            tagsList.AddRange(tagsElement.EnumerateArray()
                                .Where(item => item.ValueKind == JsonValueKind.String)
                                .Select(item => item.GetString())
                                .Where(item => !string.IsNullOrWhiteSpace(item))
                                .Select(item => item!.Trim()));
                        }
                        tagsList = tagsList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                        string coverPath = FindTemplateImagePath(dir, slug);
                        var dedupeKey = MakeSlug(displayName);
                        if (!result.ContainsKey(dedupeKey))
                        {
                            result[dedupeKey] = new CustomTemplateInfo
                            {
                                Id = slug,
                                Name = displayName,
                                Description = description,
                                Version = version,
                                EditorVersion = unityVersion,
                                ImagePath = coverPath,
                                Tags = tagsList,
                                IncludedRootFiles = includedRootFiles,
                                HasProjectNamePlaceholder = hasProjectNamePlaceholder,
                                TemplateFolderPath = dir,
                                TarballPath = File.Exists(tgzPath) ? tgzPath : string.Empty,
                                IsUnityHubTemplate = true,
                                CreatedAt = creationDate
                            };
                        }
                        // Check if package.json is missing any of the 6 keys required by Unity Hub:
                        // name, displayName, version, unity, description, dependencies
                        var hasDependencies = root.TryGetProperty("dependencies", out _);
                        var hasDisplayName = root.TryGetProperty("displayName", out _);
                        var hasDescription = root.TryGetProperty("description", out _);
                        var hasUnity = root.TryGetProperty("unity", out _);

                        if (!hasDependencies || !hasDisplayName || !hasDescription || !hasUnity)
                        {
                            try
                            {
                                var node = JsonNode.Parse(json) as JsonObject;
                                if (node is not null)
                                {
                                    if (!hasDisplayName) node["displayName"] = displayName;
                                    if (!hasDescription) node["description"] = description;
                                    if (!hasUnity) node["unity"] = unityVersion;
                                    if (!hasDependencies) node["dependencies"] = new JsonObject();
                                    WriteAllTextAtomically(packageJsonPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                                }
                            }
                            catch { }
                        }

                        // Ensure template is registered in Unity Hub's sources map if tgz exists
                        if (File.Exists(tgzPath))
                        {
                            try
                            {
                                new UnityHubTemplateSettingsService().TouchTemplateSource(tgzPath);
                            }
                            catch { }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to parse Unity Hub template at {dir}: {ex}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error scanning templates path {searchPath}: {ex}");
            }
        }

        return result.Values.OrderByDescending(t => t.CreatedAt).ToList();
    }

    public bool TemplateNameExists(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var normalizedName = name.Trim();
        var slug = MakeSlug(normalizedName);
        return GetCustomTemplates().Any(template =>
            string.Equals(template.Name.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(template.Id, slug, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<CustomTemplateInfo?> SaveAsCustomTemplateAsync(
        UnityProjectInfo sourceProject,
        string name,
        string description,
        string version,
        string? customImagePath,
        bool keepProjectSettings,
        List<string> includedRootFiles,
        bool replaceProjectName,
        IEnumerable<string>? tags = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (sourceProject is null || !Directory.Exists(sourceProject.Path)) return null;
                if (TemplateNameExists(name)) return null;

                var slug = MakeSlug(name);
                var hubTemplateFolder = Path.Combine(_unityHubTemplatesDir, slug);
                if (Directory.Exists(hubTemplateFolder)) return null;
                Directory.CreateDirectory(hubTemplateFolder);

                var normalizedVersion = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim().TrimStart('v', 'V');
                if (string.IsNullOrWhiteSpace(normalizedVersion)) normalizedVersion = "1.0.0";
                var normalizedDescription = description?.Trim() ?? string.Empty;

                var validTags = (tags ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 1. Create Unity Hub package.json
                var creationDate = DateTime.Now;
                var pkgObj = new JsonObject
                {
                    ["name"] = slug,
                    ["displayName"] = name,
                    ["version"] = normalizedVersion,
                    ["type"] = "template",
                    ["unity"] = sourceProject.Version ?? "2022.3.0f1",
                    ["description"] = normalizedDescription,
                    ["creationDate"] = creationDate.ToString("o")
                };

                if (validTags.Count > 0)
                {
                    var tagsArray = new JsonArray();
                    foreach (var t in validTags)
                    {
                        tagsArray.Add((JsonNode)JsonValue.Create(t)!);
                    }
                    pkgObj["keywords"] = tagsArray.DeepClone();
                    pkgObj["tags"] = tagsArray;
                }

                // Extract dependencies from source project's Packages/manifest.json if available.
                // Unity Hub's isValidPackage() requires the "dependencies" key to exist in
                // package.json — without it, Hub considers the manifest invalid and falls back
                // to reading from inside the .tgz, ignoring any edits to the outer file.
                var manifestPath = Path.Combine(sourceProject.Path, "Packages", "manifest.json");
                JsonObject? clonedDeps = null;
                if (File.Exists(manifestPath))
                {
                    try
                    {
                        var manifestNode = JsonNode.Parse(File.ReadAllText(manifestPath));
                        if (manifestNode?["dependencies"] is JsonObject depsObj)
                        {
                            clonedDeps = depsObj.DeepClone() as JsonObject;
                        }
                    }
                    catch { }
                }
                pkgObj["dependencies"] = clonedDeps ?? new JsonObject();

                    // Resolve selected root files before writing the manifest so the
                    // metadata and archive always describe the same payload.
                    var resolvedRootFiles = ResolveRootFiles(sourceProject.Path, includedRootFiles);
                    var copiedRootFiles = resolvedRootFiles.Select(file => file.FileName).ToList();

                    if (copiedRootFiles.Count > 0)
                    {
                        var rootFilesJson = new JsonArray();
                        foreach (var rootFile in copiedRootFiles)
                        {
                            rootFilesJson.Add((JsonNode)JsonValue.Create(rootFile)!);
                        }

                        pkgObj["rootFiles"] = rootFilesJson;
                        if (replaceProjectName)
                        {
                            pkgObj["hasProjectNamePlaceholder"] = true;
                        }
                    }

                    var packageJsonContent = pkgObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

                    // Resolve the cover destination, but do not publish any template
                    // metadata until the archive has been completed successfully.
                    string savedImagePath = string.Empty;
                    string archiveImageName = string.Empty;
                    if (!string.IsNullOrWhiteSpace(customImagePath) && File.Exists(customImagePath))
                    {
                        var ext = Path.GetExtension(customImagePath).ToLowerInvariant();
                        archiveImageName = $"{slug}{ext}";
                        savedImagePath = Path.Combine(hubTemplateFolder, archiveImageName);
                    }

                    // Stream the source project directly into gzip. The former staging
                    // + raw-tar pipeline required more than twice the project size in
                    // %TEMP%, which failed for large projects on low-free-space drives.
                    var tgzPath = Path.Combine(hubTemplateFolder, $"{slug}.tgz");
                    var temporaryTgzPath = Path.Combine(hubTemplateFolder, $".{slug}.{Guid.NewGuid():N}.tmp");
                    try
                    {
                        CreateTarGzFromProject(
                            sourceProject.Path,
                            temporaryTgzPath,
                            packageJsonContent,
                            customImagePath,
                            archiveImageName,
                            keepProjectSettings,
                            resolvedRootFiles,
                            replaceProjectName);
                        File.Move(temporaryTgzPath, tgzPath, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(temporaryTgzPath))
                        {
                            try { File.Delete(temporaryTgzPath); } catch { }
                        }
                    }

                    File.WriteAllText(Path.Combine(hubTemplateFolder, "package.json"), packageJsonContent);
                    if (!string.IsNullOrWhiteSpace(savedImagePath) &&
                        !string.IsNullOrWhiteSpace(customImagePath))
                    {
                        try
                        {
                            File.Copy(customImagePath, savedImagePath, overwrite: true);
                        }
                        catch
                        {
                            savedImagePath = string.Empty;
                        }
                    }

                    var info = new CustomTemplateInfo
                    {
                        Id = slug,
                        Name = name,
                        Description = normalizedDescription,
                        Version = normalizedVersion,
                        EditorVersion = sourceProject.Version ?? "2022.3.0f1",
                        ImagePath = savedImagePath,
                        Tags = validTags,
                        KeepProjectSettings = keepProjectSettings,
                        IncludedRootFiles = copiedRootFiles,
                        HasProjectNamePlaceholder = replaceProjectName && copiedRootFiles.Count > 0,
                        TemplateFolderPath = hubTemplateFolder,
                        TarballPath = tgzPath,
                        IsUnityHubTemplate = true,
                        CreatedAt = creationDate
                    };

                    // Register in Unity Hub's templatesSettings.json sources map so
                    // Unity Hub recognises this template without a manual rescan.
                    try
                    {
                        new UnityHubTemplateSettingsService().RegisterTemplateSource(
                            tgzPath,
                            sourceProject.Path,
                            sourceProject.Version ?? "");
                    }
                    catch { }

                    return info;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveAsCustomTemplateAsync failed: {ex}");
                try
                {
                    var slug = MakeSlug(name);
                    var failedTemplateFolder = Path.Combine(_unityHubTemplatesDir, slug);
                    if (Directory.Exists(failedTemplateFolder))
                    {
                        Directory.Delete(failedTemplateFolder, recursive: true);
                    }
                }
                catch { }
                return null;
            }
        });
    }

    public async Task<CustomTemplateInfo?> UpdateCustomTemplateAsync(
        CustomTemplateInfo template,
        string description,
        string version,
        string? replacementImagePath,
        bool removeImage,
        IEnumerable<string>? tags = null)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (template is null ||
                    string.IsNullOrWhiteSpace(template.TemplateFolderPath) ||
                    !Directory.Exists(template.TemplateFolderPath))
                {
                    return null;
                }

                var packageJsonPath = Path.Combine(template.TemplateFolderPath, "package.json");
                if (!File.Exists(packageJsonPath))
                {
                    return null;
                }

                var packageObject = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
                if (packageObject is null)
                {
                    return null;
                }

                var slug = packageObject["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(slug))
                {
                    slug = template.Id;
                }
                slug = MakeSlug(slug);

                var normalizedVersion = string.IsNullOrWhiteSpace(version) ? (template.Version ?? "1.0.0") : version.Trim().TrimStart('v', 'V');
                if (string.IsNullOrWhiteSpace(normalizedVersion)) normalizedVersion = "1.0.0";
                var normalizedDescription = description?.Trim() ?? string.Empty;

                packageObject["description"] = normalizedDescription;
                packageObject["version"] = normalizedVersion;

                var validTags = (tags ?? template.Tags ?? [])
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .Select(t => t.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var tagsArray = new JsonArray();
                foreach (var t in validTags)
                {
                    tagsArray.Add((JsonNode)JsonValue.Create(t)!);
                }
                packageObject["keywords"] = tagsArray.DeepClone();
                packageObject["tags"] = tagsArray;

                // Unity Hub's isValidPackage() requires ALL of: name, displayName,
                // version, unity, description, dependencies.  Ensure these keys are
                // present so Hub reads the outer package.json instead of falling back
                // to the (now stale) copy inside the .tgz archive.
                if (packageObject["displayName"] is null)
                {
                    packageObject["displayName"] = packageObject["name"]?.GetValue<string>() ?? template.Name;
                }
                if (packageObject["unity"] is null)
                {
                    packageObject["unity"] = template.EditorVersion ?? "";
                }
                if (packageObject["dependencies"] is null)
                {
                    packageObject["dependencies"] = new JsonObject();
                }

                var packageJsonContent = packageObject.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

                var currentImagePath = FindTemplateImagePath(template.TemplateFolderPath, slug);
                var updatedImagePath = removeImage ? string.Empty : currentImagePath;
                var archiveImageSourcePath = updatedImagePath;

                if (!string.IsNullOrWhiteSpace(replacementImagePath))
                {
                    if (!File.Exists(replacementImagePath))
                    {
                        return null;
                    }

                    var extension = Path.GetExtension(replacementImagePath).ToLowerInvariant();
                    if (!TemplateImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
                    {
                        return null;
                    }

                    updatedImagePath = Path.Combine(template.TemplateFolderPath, $"{slug}{extension}");
                    archiveImageSourcePath = replacementImagePath;
                }

                var tarballPath = template.TarballPath;
                if (string.IsNullOrWhiteSpace(tarballPath))
                {
                    tarballPath = Path.Combine(template.TemplateFolderPath, $"{slug}.tgz");
                }

                if (File.Exists(tarballPath))
                {
                    RewriteTarGz(
                        tarballPath,
                        packageJsonContent,
                        slug,
                        archiveImageSourcePath,
                        string.IsNullOrWhiteSpace(updatedImagePath) ? string.Empty : Path.GetFileName(updatedImagePath));
                }

                if (!string.IsNullOrWhiteSpace(replacementImagePath) &&
                    !string.Equals(
                        Path.GetFullPath(replacementImagePath),
                        Path.GetFullPath(updatedImagePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    CopyFileAtomically(replacementImagePath, updatedImagePath);
                }

                WriteAllTextAtomically(packageJsonPath, packageJsonContent);
                DeleteSupersededTemplateImages(template.TemplateFolderPath, slug, updatedImagePath);

                // Notify Unity Hub's templatesSettings.json sources registry about the update
                try
                {
                    if (File.Exists(tarballPath))
                    {
                        new UnityHubTemplateSettingsService().TouchTemplateSource(tarballPath);
                    }
                }
                catch { }

                template.Description = normalizedDescription;
                template.Version = normalizedVersion;
                template.ImagePath = updatedImagePath;
                template.Tags = validTags;
                template.TarballPath = File.Exists(tarballPath) ? tarballPath : string.Empty;
                return template;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdateCustomTemplateAsync failed: {ex}");
                return null;
            }
        });
    }

    public async Task<bool> CreateProjectFromTemplateAsync(CustomTemplateInfo template, string targetProjectPath, string targetEditorVersion)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (template is null) return false;

                Directory.CreateDirectory(targetProjectPath);

                // Case A: Extract from .tgz tarball if present (Official Unity Hub template format)
                if (!string.IsNullOrWhiteSpace(template.TarballPath) && File.Exists(template.TarballPath))
                {
                    ExtractTarGz(template.TarballPath, targetProjectPath);
                }
                // Case B: Copy from uncompressed template folder
                else if (!string.IsNullOrEmpty(template.TemplateFolderPath) && Directory.Exists(template.TemplateFolderPath))
                {
                    CopyDirectory(template.TemplateFolderPath, targetProjectPath);
                }
                else
                {
                    return false;
                }

                if (template.HasProjectNamePlaceholder && template.IncludedRootFiles.Count > 0)
                {
                    var projectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetProjectPath));
                    foreach (var rootFile in template.IncludedRootFiles
                                 .Where(IsSafeRootFileName)
                                 .Distinct(StringComparer.Ordinal)
                                 .Take(MaximumRootFileCount))
                    {
                        SubstituteProjectName(Path.Combine(targetProjectPath, rootFile), projectName);
                    }
                }

                // Ensure ProjectSettings/ProjectVersion.txt is updated with chosen targetEditorVersion
                var projectSettingsDir = Path.Combine(targetProjectPath, "ProjectSettings");
                Directory.CreateDirectory(projectSettingsDir);
                var versionFile = Path.Combine(projectSettingsDir, "ProjectVersion.txt");
                File.WriteAllText(versionFile, $"m_EditorVersion: {targetEditorVersion}\r\nm_EditorVersionWithRevision: {targetEditorVersion}\r\n");

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CreateProjectFromTemplateAsync failed: {ex}");
                return false;
            }
        });
    }

    public bool DeleteCustomTemplate(string templateId)
    {
        try
        {
            var templates = GetCustomTemplates();
            var target = templates.FirstOrDefault(t => t.Id == templateId || MakeSlug(t.Name) == templateId);
            if (target is not null && Directory.Exists(target.TemplateFolderPath))
            {
                if (!string.IsNullOrWhiteSpace(target.TarballPath))
                {
                    try
                    {
                        new UnityHubTemplateSettingsService().UnregisterTemplateSource(target.TarballPath);
                    }
                    catch { }
                }

                Directory.Delete(target.TemplateFolderPath, recursive: true);
            }
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"DeleteCustomTemplate failed: {ex}");
            return false;
        }
    }

    private readonly record struct RootFileCandidate(string FileName, string SourcePath, long Size);

    private static IReadOnlyList<RootFileCandidate> ResolveRootFiles(
        string projectPath,
        IEnumerable<string>? requestedRootFiles)
    {
        if (requestedRootFiles is null)
        {
            return [];
        }

        var projectRoot = Path.GetFullPath(projectPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var projectRootPrefix = projectRoot + Path.DirectorySeparatorChar;
        var selected = new List<RootFileCandidate>();
        long selectedBytes = 0;

        foreach (var fileName in requestedRootFiles
                     .Where(IsSafeRootFileName)
                     .Distinct(StringComparer.Ordinal)
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            if (selected.Count >= MaximumRootFileCount)
            {
                break;
            }

            var sourcePath = Path.GetFullPath(Path.Combine(projectRoot, fileName));
            if (!sourcePath.StartsWith(projectRootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var file = new FileInfo(sourcePath);
            if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (selectedBytes + file.Length > MaximumRootFilesTotalBytes)
            {
                continue;
            }

            selectedBytes += file.Length;
            selected.Add(new RootFileCandidate(fileName, sourcePath, file.Length));
        }

        return selected;
    }

    private static bool IsSafeRootFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName is "." or ".." ||
            Path.IsPathRooted(fileName) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.EndsWith(' ') ||
            fileName.EndsWith('.'))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        return !Regex.IsMatch(
            stem,
            @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static void TokenizeProjectName(string filePath, string sourceProjectName)
    {
        if (string.IsNullOrWhiteSpace(sourceProjectName) || sourceProjectName is "." or "..")
        {
            return;
        }

        TransformSmallUtf8File(
            filePath,
            content => Regex.Replace(
                content,
                $@"\b{Regex.Escape(sourceProjectName.Trim())}\b",
                ProjectNameToken,
                RegexOptions.CultureInvariant));
    }

    private static void SubstituteProjectName(string filePath, string projectName)
    {
        if (string.IsNullOrWhiteSpace(projectName))
        {
            return;
        }

        TransformSmallUtf8File(
            filePath,
            content => content.Replace(ProjectNameToken, projectName, StringComparison.Ordinal));
    }

    private static void TransformSmallUtf8File(string filePath, Func<string, string> transform)
    {
        try
        {
            var file = new FileInfo(filePath);
            if (!file.Exists ||
                file.Length > MaximumPlaceholderFileBytes ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            var bytes = File.ReadAllBytes(filePath);
            if (LooksLikeBinaryOrUtf16(bytes))
            {
                return;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var original = utf8.GetString(bytes);
            var transformed = transform(original);
            if (!string.Equals(original, transformed, StringComparison.Ordinal))
            {
                File.WriteAllText(filePath, transformed, new UTF8Encoding(false));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            System.Diagnostics.Debug.WriteLine($"Skipped root-file placeholder substitution for {filePath}: {ex.Message}");
        }
    }

    private static bool LooksLikeBinaryOrUtf16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 &&
            ((bytes[0] == 0xFF && bytes[1] == 0xFE) ||
             (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return true;
        }

        if (bytes.Length >= 4 &&
            ((bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF) ||
             (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)))
        {
            return true;
        }

        return bytes.Contains((byte)0);
    }

    private static void ExtractTarGz(string tgzFilePath, string outputDirectory)
    {
        using var fileStream = File.OpenRead(tgzFilePath);
        using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        using var tarReader = new TarReader(gzipStream);

        while (tarReader.GetNextEntry() is { } entry)
        {
            var name = entry.Name.Replace('\\', '/');
            string relativePath;

            if (name.StartsWith("package/ProjectData~/"))
            {
                relativePath = name["package/ProjectData~/".Length..];
            }
            else if (name.StartsWith("package/ProjectData/"))
            {
                relativePath = name["package/ProjectData/".Length..];
            }
            else
            {
                // Unity Hub stores package.json, the template image, and
                // attestation data at the package root. They describe the
                // template and must not be copied into the created project.
                continue;
            }

            if (string.IsNullOrWhiteSpace(relativePath)) continue;

            var targetPath = GetSafeExtractionPath(outputDirectory, relativePath);

            if (entry.EntryType == TarEntryType.Directory)
            {
                Directory.CreateDirectory(targetPath);
            }
            else if (entry.EntryType == TarEntryType.RegularFile || entry.EntryType == TarEntryType.V7RegularFile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                entry.ExtractToFile(targetPath, overwrite: true);
            }
        }
    }

    private static void CreateTarGzFromProject(
        string projectPath,
        string outputTgzPath,
        string packageJsonContent,
        string? imageSourcePath,
        string imageEntryFileName,
        bool keepProjectSettings,
        IReadOnlyList<RootFileCandidate> rootFiles,
        bool replaceProjectName)
    {
        using var targetStream = new FileStream(
            outputTgzPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 1024 * 1024,
            FileOptions.SequentialScan);
        using var gzipStream = new GZipStream(targetStream, CompressionLevel.Optimal, leaveOpen: false);
        using var tarWriter = new TarWriter(gzipStream, TarEntryFormat.Pax, leaveOpen: false);

        WriteMemoryEntry(
            tarWriter,
            "package/package.json",
            Encoding.UTF8.GetBytes(packageJsonContent));

        if (!string.IsNullOrWhiteSpace(imageSourcePath) &&
            !string.IsNullOrWhiteSpace(imageEntryFileName) &&
            File.Exists(imageSourcePath))
        {
            WriteFileEntry(tarWriter, imageSourcePath, $"package/{imageEntryFileName}");
        }

        WriteProjectDirectory(tarWriter, Path.Combine(projectPath, "Assets"), "Assets");
        WriteProjectDirectory(tarWriter, Path.Combine(projectPath, "Packages"), "Packages");
        if (keepProjectSettings)
        {
            WriteProjectDirectory(tarWriter, Path.Combine(projectPath, "ProjectSettings"), "ProjectSettings");
        }

        var sourceProjectName = Path.GetFileName(Path.TrimEndingDirectorySeparator(projectPath));
        foreach (var rootFile in rootFiles)
        {
            var entryName = $"package/ProjectData~/{rootFile.FileName}";
            if (replaceProjectName && TryGetTokenizedRootFile(rootFile, sourceProjectName, out var transformedBytes))
            {
                WriteMemoryEntry(tarWriter, entryName, transformedBytes);
            }
            else
            {
                WriteFileEntry(tarWriter, rootFile.SourcePath, entryName);
            }
        }
    }

    private static void WriteProjectDirectory(TarWriter writer, string sourceDirectory, string archiveDirectory)
    {
        var root = new DirectoryInfo(sourceDirectory);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            return;
        }

        WriteProjectDirectory(writer, root, archiveDirectory);
    }

    private static void WriteProjectDirectory(TarWriter writer, DirectoryInfo directory, string archiveDirectory)
    {
        FileInfo[] files;
        try { files = directory.GetFiles(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
        {
            if (file.Name.StartsWith('.') ||
                file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            WriteFileEntry(writer, file.FullName, $"package/ProjectData~/{archiveDirectory}/{file.Name}");
        }

        DirectoryInfo[] subdirectories;
        try { subdirectories = directory.GetDirectories(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        foreach (var subdirectory in subdirectories.OrderBy(directory => directory.Name, StringComparer.Ordinal))
        {
            if (ShouldSkipTemplateDirectory(subdirectory))
            {
                continue;
            }

            WriteProjectDirectory(writer, subdirectory, $"{archiveDirectory}/{subdirectory.Name}");
        }
    }

    private static bool ShouldSkipTemplateDirectory(DirectoryInfo directory)
        => directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
           directory.Name.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals("Obj", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals("Builds", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
           directory.Name.Equals(".idea", StringComparison.OrdinalIgnoreCase);

    private static void WriteFileEntry(TarWriter writer, string sourcePath, string entryName)
    {
        FileStream sourceStream;
        try
        {
            sourceStream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Skipped unreadable template file {sourcePath}: {ex.Message}");
            return;
        }

        using (sourceStream)
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = sourceStream,
                ModificationTime = File.GetLastWriteTimeUtc(sourcePath)
            };
            writer.WriteEntry(entry);
        }
    }

    private static void WriteMemoryEntry(TarWriter writer, string entryName, byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
        {
            DataStream = stream
        };
        writer.WriteEntry(entry);
    }

    private static bool TryGetTokenizedRootFile(
        RootFileCandidate rootFile,
        string sourceProjectName,
        out byte[] transformedBytes)
    {
        transformedBytes = [];
        if (rootFile.Size > MaximumPlaceholderFileBytes || string.IsNullOrWhiteSpace(sourceProjectName))
        {
            return false;
        }

        try
        {
            var bytes = File.ReadAllBytes(rootFile.SourcePath);
            if (LooksLikeBinaryOrUtf16(bytes))
            {
                return false;
            }

            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var original = utf8.GetString(bytes);
            var transformed = Regex.Replace(
                original,
                $@"\b{Regex.Escape(sourceProjectName.Trim())}\b",
                ProjectNameToken,
                RegexOptions.CultureInvariant);
            transformedBytes = utf8.GetBytes(transformed);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            System.Diagnostics.Debug.WriteLine($"Skipped root-file placeholder substitution for {rootFile.SourcePath}: {ex.Message}");
            return false;
        }
    }

    private static void RewriteTarGz(
        string tarballPath,
        string packageJsonContent,
        string slug,
        string imageSourcePath,
        string imageEntryFileName)
    {
        var temporaryTarPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tar");
        var temporaryTgzPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tgz");

        try
        {
            using (var inputFile = File.OpenRead(tarballPath))
            using (var inputGzip = new GZipStream(inputFile, CompressionMode.Decompress))
            using (var reader = new TarReader(inputGzip))
            using (var writer = new TarWriter(File.Create(temporaryTarPath)))
            {
                var manifestBytes = Encoding.UTF8.GetBytes(packageJsonContent);
                using var manifestStream = new MemoryStream(manifestBytes, writable: false);
                var manifestEntry = new PaxTarEntry(TarEntryType.RegularFile, "package/package.json")
                {
                    DataStream = manifestStream
                };
                writer.WriteEntry(manifestEntry);

                if (!string.IsNullOrWhiteSpace(imageSourcePath) &&
                    !string.IsNullOrWhiteSpace(imageEntryFileName) &&
                    File.Exists(imageSourcePath))
                {
                    writer.WriteEntry(imageSourcePath, $"package/{imageEntryFileName}");
                }

                while (reader.GetNextEntry() is { } entry)
                {
                    var entryName = entry.Name.Replace('\\', '/');
                    if (entryName.Equals("package/package.json", StringComparison.OrdinalIgnoreCase) ||
                        entryName.Equals("package/.attestation.p7m", StringComparison.OrdinalIgnoreCase) ||
                        IsPackageRootTemplateImage(entryName, slug))
                    {
                        continue;
                    }

                    writer.WriteEntry(entry);
                }
            }

            using (var rawTarStream = File.OpenRead(temporaryTarPath))
            using (var targetStream = File.Create(temporaryTgzPath))
            using (var gzipStream = new GZipStream(targetStream, CompressionLevel.Optimal))
            {
                rawTarStream.CopyTo(gzipStream);
            }

            File.Move(temporaryTgzPath, tarballPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryTarPath)) File.Delete(temporaryTarPath);
            if (File.Exists(temporaryTgzPath)) File.Delete(temporaryTgzPath);
        }
    }

    private static bool IsPackageRootTemplateImage(string entryName, string slug)
    {
        const string packagePrefix = "package/";
        if (!entryName.StartsWith(packagePrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relativeName = entryName[packagePrefix.Length..];
        if (relativeName.Contains('/'))
        {
            return false;
        }

        var extension = Path.GetExtension(relativeName);
        if (!TemplateImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(relativeName);
        return string.Equals(baseName, slug, StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("preview", StringComparison.OrdinalIgnoreCase) ||
               baseName.Equals("icon", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteAllTextAtomically(string targetPath, string content)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void CopyFileAtomically(string sourcePath, string targetPath)
    {
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(targetPath)!,
            $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: true);
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void DeleteSupersededTemplateImages(string templateDirectory, string slug, string imageToKeep)
    {
        var keepFullPath = string.IsNullOrWhiteSpace(imageToKeep)
            ? string.Empty
            : Path.GetFullPath(imageToKeep);

        var candidateNames = TemplateImageExtensions.Select(extension => $"{slug}{extension}")
            .Concat(new[]
            {
                "cover.png", "cover.jpg", "cover.jpeg", "cover.webp",
                "preview.png", "icon.png"
            });

        foreach (var candidateName in candidateNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidatePath = Path.Combine(templateDirectory, candidateName);
            if (!File.Exists(candidatePath) ||
                string.Equals(Path.GetFullPath(candidatePath), keepFullPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Delete(candidatePath);
        }
    }

    private static string FindTemplateImagePath(string templateDirectory, string slug)
    {
        foreach (var extension in TemplateImageExtensions)
        {
            var candidate = Path.Combine(templateDirectory, $"{slug}{extension}");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var legacyName in new[]
        {
            "cover.png", "cover.jpg", "cover.jpeg", "cover.webp",
            "preview.png", "icon.png"
        })
        {
            var candidate = Path.Combine(templateDirectory, legacyName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static string GetSafeExtractionPath(string outputDirectory, string relativePath)
    {
        var outputRoot = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var targetPath = Path.GetFullPath(Path.Combine(outputRoot, relativePath));

        if (!targetPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Template archive entry escapes the target directory: {relativePath}");
        }

        return targetPath;
    }

    private static string MakeSlug(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "custom-template";
        var slug = System.Text.RegularExpressions.Regex.Replace(text.ToLowerInvariant(), @"[^a-z0-9\-]", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "custom-template" : slug;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        try
        {
            var dir = new DirectoryInfo(sourceDir);
            if (!dir.Exists) return;

            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

            Directory.CreateDirectory(destinationDir);

            FileInfo[] files;
            try
            {
                files = dir.GetFiles();
            }
            catch
            {
                return;
            }

            foreach (var file in files)
            {
                try
                {
                    if (file.Name.StartsWith(".") || file.Name.EndsWith(".tmp") || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    file.CopyTo(Path.Combine(destinationDir, file.Name), overwrite: true);
                }
                catch
                {
                }
            }

            DirectoryInfo[] subDirs;
            try
            {
                subDirs = dir.GetDirectories();
            }
            catch
            {
                return;
            }

            foreach (var subDir in subDirs)
            {
                try
                {
                    if (subDir.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                        subDir.Name.Equals("Library", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals("Temp", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals("Obj", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals("Logs", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals("Build", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals("Builds", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        subDir.Name.Equals(".idea", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    CopyDirectory(subDir.FullName, Path.Combine(destinationDir, subDir.Name));
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
