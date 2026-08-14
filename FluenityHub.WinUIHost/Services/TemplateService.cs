using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Formats.Tar;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FluenityHub_WinUIHost.Models;

namespace FluenityHub_WinUIHost.Services;

public sealed class TemplateService
{
    private static readonly string[] TemplateImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];
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

                        string slug = root.TryGetProperty("name", out var n) ? n.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                        string displayName = root.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? slug : slug;
                        string version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "1.0.0" : "1.0.0";
                        string unityVersion = root.TryGetProperty("unity", out var u) ? u.GetString() ?? "" : "";
                        string description = root.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
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
                                TemplateFolderPath = dir,
                                TarballPath = File.Exists(tgzPath) ? tgzPath : string.Empty,
                                IsUnityHubTemplate = true,
                                CreatedAt = creationDate
                            };
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
        List<string> includedRootFiles)
    {
        return await Task.Run(() =>
        {
            try
            {
                if (sourceProject is null || !Directory.Exists(sourceProject.Path)) return null;
                if (TemplateNameExists(name)) return null;

                var slug = MakeSlug(name);
                var hubTemplateFolder = Path.Combine(_unityHubTemplatesDir, slug);
                Directory.CreateDirectory(hubTemplateFolder);

                // 1. Create Unity Hub package.json
                var creationDate = DateTime.Now;
                var pkgObj = new JsonObject
                {
                    ["name"] = slug,
                    ["displayName"] = name,
                    ["version"] = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                    ["type"] = "template",
                    ["unity"] = sourceProject.Version ?? "2022.3.0f1",
                    ["description"] = description,
                    ["creationDate"] = creationDate.ToString("o")
                };

                var packageJsonContent = pkgObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(hubTemplateFolder, "package.json"), packageJsonContent);

                // Create staging directory for ProjectData contents
                var stagingDir = Path.Combine(Path.GetTempPath(), $"FluenityTemplateStaging_{Guid.NewGuid():N}");
                Directory.CreateDirectory(stagingDir);

                try
                {
                    // 2. Copy Assets directory safely
                    var srcAssets = Path.Combine(sourceProject.Path, "Assets");
                    if (Directory.Exists(srcAssets))
                    {
                        CopyDirectory(srcAssets, Path.Combine(stagingDir, "Assets"));
                    }

                    // 3. Copy Packages directory (manifest.json)
                    var srcPackages = Path.Combine(sourceProject.Path, "Packages");
                    if (Directory.Exists(srcPackages))
                    {
                        CopyDirectory(srcPackages, Path.Combine(stagingDir, "Packages"));
                    }

                    // 4. Copy ProjectSettings if selected
                    if (keepProjectSettings)
                    {
                        var srcSettings = Path.Combine(sourceProject.Path, "ProjectSettings");
                        if (Directory.Exists(srcSettings))
                        {
                            CopyDirectory(srcSettings, Path.Combine(stagingDir, "ProjectSettings"));
                        }
                    }

                    // 5. Copy included root files
                    if (includedRootFiles is not null)
                    {
                        foreach (var fileRelPath in includedRootFiles)
                        {
                            if (string.IsNullOrWhiteSpace(fileRelPath)) continue;
                            var srcFilePath = Path.Combine(sourceProject.Path, fileRelPath);
                            if (File.Exists(srcFilePath))
                            {
                                var destFilePath = Path.Combine(stagingDir, fileRelPath);
                                Directory.CreateDirectory(Path.GetDirectoryName(destFilePath)!);
                                File.Copy(srcFilePath, destFilePath, overwrite: true);
                            }
                        }
                    }

                    // 6. Save cover image if provided
                    string savedImagePath = string.Empty;
                    if (!string.IsNullOrWhiteSpace(customImagePath) && File.Exists(customImagePath))
                    {
                        try
                        {
                            var ext = Path.GetExtension(customImagePath).ToLowerInvariant();
                            savedImagePath = Path.Combine(hubTemplateFolder, $"{slug}{ext}");
                            File.Copy(customImagePath, savedImagePath, overwrite: true);
                        }
                        catch
                        {
                            savedImagePath = string.Empty;
                        }
                    }

                    // 7. Create .tgz tarball archive matching Unity Hub format
                    var tgzPath = Path.Combine(hubTemplateFolder, $"{slug}.tgz");
                    CreateTarGz(stagingDir, tgzPath, packageJsonContent, savedImagePath);

                    var info = new CustomTemplateInfo
                    {
                        Id = slug,
                        Name = name,
                        Description = description,
                        Version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version,
                        EditorVersion = sourceProject.Version ?? "2022.3.0f1",
                        ImagePath = savedImagePath,
                        KeepProjectSettings = keepProjectSettings,
                        IncludedRootFiles = includedRootFiles ?? [],
                        TemplateFolderPath = hubTemplateFolder,
                        TarballPath = tgzPath,
                        IsUnityHubTemplate = true,
                        CreatedAt = creationDate
                    };

                    return info;
                }
                finally
                {
                    if (Directory.Exists(stagingDir))
                    {
                        try { Directory.Delete(stagingDir, recursive: true); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"SaveAsCustomTemplateAsync failed: {ex}");
                return null;
            }
        });
    }

    public async Task<CustomTemplateInfo?> UpdateCustomTemplateAsync(
        CustomTemplateInfo template,
        string description,
        string version,
        string? replacementImagePath,
        bool removeImage)
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

                packageObject["description"] = description;
                packageObject["version"] = version;
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

                template.Description = description;
                template.Version = version;
                template.ImagePath = updatedImagePath;
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

    private static void CreateTarGz(
        string templateFolderPath,
        string outputTgzPath,
        string packageJsonContent,
        string savedImagePath)
    {
        var tempTarPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tar");
        try
        {
            using (var tarWriter = new TarWriter(File.Create(tempTarPath)))
            {
                var tempPkgJsonFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
                File.WriteAllText(tempPkgJsonFile, packageJsonContent);
                try
                {
                    tarWriter.WriteEntry(tempPkgJsonFile, "package/package.json");
                }
                finally
                {
                    if (File.Exists(tempPkgJsonFile)) File.Delete(tempPkgJsonFile);
                }

                if (!string.IsNullOrWhiteSpace(savedImagePath) && File.Exists(savedImagePath))
                {
                    tarWriter.WriteEntry(savedImagePath, $"package/{Path.GetFileName(savedImagePath)}");
                }

                foreach (var file in Directory.GetFiles(templateFolderPath, "*.*", SearchOption.AllDirectories))
                {
                    var relPath = Path.GetRelativePath(templateFolderPath, file).Replace('\\', '/');
                    var entryName = $"package/ProjectData~/{relPath}";
                    tarWriter.WriteEntry(file, entryName);
                }
            }

            using var rawTarStream = File.OpenRead(tempTarPath);
            using var targetStream = File.Create(outputTgzPath);
            using var gzStream = new GZipStream(targetStream, CompressionLevel.Optimal);
            rawTarStream.CopyTo(gzStream);
        }
        finally
        {
            if (File.Exists(tempTarPath)) File.Delete(tempTarPath);
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
