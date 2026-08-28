using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using FluenityHub_WinUIHost.Models;
using Microsoft.Data.Sqlite;

namespace FluenityHub_WinUIHost.Services;

public sealed class UnityHubProjectService
{
    private const int MaximumProjectTagCount = 50;
    private const int MaximumProjectTagLength = 50;
    private static readonly HashSet<char> ReservedProjectTagCharacters =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*', ','
    ];

    private readonly UnityHubProjectSettingsService _projectSettingsService = new();

    private static readonly string ProjectsJsonPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub",
        "projects-v1.json");

    private static readonly string HubDatabasePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "UnityHub",
        "hub.db");

    private sealed record StoredProject(string Path, string Data);

    public DateTime GetProjectStoreChangeStampUtc()
    {
        var latest = DateTime.MinValue;
        foreach (var path in new[]
                 {
                     HubDatabasePath,
                     HubDatabasePath + "-wal",
                     ProjectsJsonPath
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    var lastWrite = File.GetLastWriteTimeUtc(path);
                    if (lastWrite > latest)
                    {
                        latest = lastWrite;
                    }
                }
            }
            catch (IOException)
            {
                // A transient Hub write will be observed on the next activation.
            }
            catch (UnauthorizedAccessException)
            {
                // Keep the last successfully observed store timestamp.
            }
        }

        return latest;
    }

    public List<UnityProjectInfo> GetRecentProjects(
        bool repairProjectsFile = true,
        bool resolveProductNames = true)
    {
        var hasDatabaseProjects = TryReadProjectsFromDatabase(out var databaseProjects);
        List<StoredProject> storedProjects;
        if (hasDatabaseProjects)
        {
            var mirroredProjects = new Dictionary<string, StoredProject>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(ProjectsJsonPath))
            {
                if (repairProjectsFile)
                {
                    RepairProjectsJson();
                }

                foreach (var mirroredProject in ReadProjectsFromJson())
                {
                    mirroredProjects[mirroredProject.Path] = mirroredProject;
                }
            }

            // SQLite owns list membership. The JSON row remains an overlay for
            // FluenityHub-specific metadata until those fields are migrated.
            storedProjects = databaseProjects
                .Select(project => mirroredProjects.GetValueOrDefault(project.Path, project))
                .ToList();
        }
        else if (File.Exists(ProjectsJsonPath))
        {
            if (repairProjectsFile)
            {
                RepairProjectsJson();
            }

            storedProjects = ReadProjectsFromJson();
        }
        else
        {
            return [];
        }

        var projects = new List<UnityProjectInfo>();
        var showProductNames = _projectSettingsService.GetShowProductNames();
        var databaseTags = hasDatabaseProjects
            ? ReadTagsByProjectPath(databaseProjects)
            : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var storedProject in storedProjects)
        {
            using var projectDocument = JsonDocument.Parse(storedProject.Data);
            var projectPath = storedProject.Path;
            var projectData = projectDocument.RootElement;

            var storedTitle = ReadOptionalString(projectData, "title");
            var hasCustomDisplayName = ReadOptionalBoolean(
                projectData,
                "hasCustomDisplayName");
            var title = hasCustomDisplayName
                ? storedTitle ?? System.IO.Path.GetFileName(projectPath)
                : showProductNames
                    ? (resolveProductNames ? ParseProjectProductName(projectPath) : null)
                        ?? storedTitle
                        ?? System.IO.Path.GetFileName(projectPath)
                    : System.IO.Path.GetFileName(projectPath);
            var version = ReadOptionalString(projectData, "version") ?? "Unknown";
            var buildTarget = ReadOptionalString(projectData, "buildTarget") ?? string.Empty;
            var cloudProjectId = ReadOptionalString(projectData, "cloudProjectId");
            var organizationId = ReadOptionalString(projectData, "organizationId");
            var localProjectId = ReadOptionalString(projectData, "localProjectId");
            var lastModifiedMilliseconds = ReadOptionalInt64(projectData, "lastModified");
            var isFavorite = ReadOptionalBoolean(projectData, "isFavorite");

            var commandLineArguments = ReadOptionalString(projectData, "commandLineArguments");
            var configuredSourceControlProvider = ReadOptionalString(projectData, "vcsProvider");
            var configuredSourceControlPath = ReadOptionalString(projectData, "vcsConfigurationPath");
            var configuredSourceControlOrganization = ReadOptionalString(projectData, "organizationName");
            var configuredSourceControlRepository = ReadOptionalString(projectData, "repositoryName");
            var projectPathInsideRepository = ReadOptionalString(projectData, "projectPathInsideRepository");
            var isSourceControlDisconnected = ReadOptionalBoolean(projectData, "vcsDisconnected");
            var isVersionControlConnected = ReadOptionalBoolean(projectData, "isVersionControlConnected");
            var group = ReadOptionalString(projectData, "group");
            var tags = databaseTags.TryGetValue(projectPath, out var currentTags)
                ? currentTags
                : NormalizeProjectTags(ReadOptionalStringArray(projectData, "tags"));

            projects.Add(new UnityProjectInfo
            {
                Path = projectPath,
                Title = title,
                Version = version,
                BuildTarget = buildTarget,
                CloudProjectId = cloudProjectId,
                OrganizationId = organizationId,
                LocalProjectId = localProjectId,
                LastModifiedUtc = DateTimeOffset.FromUnixTimeMilliseconds(lastModifiedMilliseconds).UtcDateTime,
                IsFavorite = isFavorite,
                ConfiguredSourceControlProvider = configuredSourceControlProvider,
                ConfiguredSourceControlPath = configuredSourceControlPath,
                ConfiguredSourceControlOrganization = configuredSourceControlOrganization,
                ConfiguredSourceControlRepository = configuredSourceControlRepository,
                ProjectPathInsideRepository = projectPathInsideRepository,
                IsSourceControlDisconnected = isSourceControlDisconnected,
                IsVersionControlConnected = isVersionControlConnected,
                CommandLineArguments = commandLineArguments,
                Group = string.IsNullOrWhiteSpace(group) ? "Ungrouped" : group,
                Tags = tags
            });
        }

        return projects;
    }

    /// <summary>
    /// Applies the same normalization contract used by Unity Hub before tags
    /// are written to projects-v1.json while preserving the user's tag order.
    /// </summary>
    public static List<string> NormalizeProjectTags(IEnumerable<string>? tags)
    {
        if (tags is null)
        {
            return [];
        }

        return tags
            .Select(NormalizeProjectTag)
            .Where(static tag => tag.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Take(MaximumProjectTagCount)
            .ToList();
    }

    public bool UpdateProjectTags(string projectPath, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var normalizedTags = NormalizeProjectTags(tags);
        var databaseResult = TryUpdateProjectTagsInDatabase(projectPath, normalizedTags);
        if (databaseResult.HasValue)
        {
            return databaseResult.Value;
        }

        if (!File.Exists(ProjectsJsonPath))
        {
            return false;
        }

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode?["data"] is not JsonObject dataObj
                || !dataObj.TryGetPropertyValue(projectPath, out var projectNode)
                || projectNode is not JsonObject projectObject)
            {
                return false;
            }

            var tagsArray = new JsonArray();
            foreach (var tag in normalizedTags)
            {
                tagsArray.Add((JsonNode)JsonValue.Create(tag)!);
            }

            projectObject["tags"] = tagsArray;
            var options = AppJsonContext.Default.Options;
            CreateBackup();
            WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static List<StoredProject> ReadProjectsFromJson()
    {
        var content = ReadAllTextShared(ProjectsJsonPath);
        using var document = JsonDocument.Parse(content);
        if (!document.RootElement.TryGetProperty("data", out var dataElement)
            || dataElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Unity Hub projects file is missing the 'data' object.");
        }

        return dataElement.EnumerateObject()
            .Select(static property => new StoredProject(property.Name, property.Value.GetRawText()))
            .ToList();
    }

    private static Dictionary<string, List<string>> ReadTagsByProjectPath(
        IEnumerable<StoredProject> projects)
    {
        var tagsByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            try
            {
                using var document = JsonDocument.Parse(project.Data);
                tagsByPath[project.Path] = NormalizeProjectTags(
                    ReadOptionalStringArray(document.RootElement, "tags"));
            }
            catch (JsonException)
            {
                // A corrupt database row must not hide the remaining projects.
            }
        }

        return tagsByPath;
    }

    private static bool TryReadProjectsFromDatabase(out List<StoredProject> projects)
    {
        projects = [];
        if (!File.Exists(HubDatabasePath))
        {
            return false;
        }

        try
        {
            using var connection = OpenHubDatabase(SqliteOpenMode.ReadOnly);
            if (!HasProjectsTable(connection))
            {
                return false;
            }

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, data FROM projects ORDER BY path";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                projects.Add(new StoredProject(reader.GetString(0), reader.GetString(1)));
            }

            // An empty table becomes authoritative only after Unity Hub's
            // one-time JSON migration has completed.
            return projects.Count > 0 || HasProjectsMigrationMarker(connection);
        }
        catch (SqliteException)
        {
            projects = [];
            return false;
        }
    }

    /// <summary>
    /// Returns null when this Unity Hub installation does not use hub.db,
    /// otherwise returns whether the authoritative SQLite update succeeded.
    /// </summary>
    private static bool? TryUpdateProjectTagsInDatabase(
        string projectPath,
        IReadOnlyCollection<string> normalizedTags)
    {
        if (!File.Exists(HubDatabasePath))
        {
            return null;
        }

        try
        {
            using var connection = OpenHubDatabase(SqliteOpenMode.ReadWrite);
            if (!HasProjectsTable(connection))
            {
                return null;
            }

            ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
            try
            {
                using var readCommand = connection.CreateCommand();
                readCommand.CommandText = "SELECT data FROM projects WHERE path = $path";
                readCommand.Parameters.AddWithValue("$path", projectPath);
                var storedData = readCommand.ExecuteScalar() as string;
                if (storedData is null)
                {
                    ExecuteNonQuery(connection, "ROLLBACK");
                    return HasProjectsMigrationMarker(connection) || HasAnyProjects(connection)
                        ? false
                        : null;
                }

                var projectObject = JsonNode.Parse(storedData)?.AsObject();
                if (projectObject is null)
                {
                    ExecuteNonQuery(connection, "ROLLBACK");
                    return false;
                }

                var tagsArray = new JsonArray();
                foreach (var tag in normalizedTags)
                {
                    tagsArray.Add((JsonNode)JsonValue.Create(tag)!);
                }

                projectObject["tags"] = tagsArray;
                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = """
                    UPDATE projects
                    SET data = $data, updated_at = $updatedAt
                    WHERE path = $path
                    """;
                updateCommand.Parameters.AddWithValue(
                    "$data",
                    projectObject.ToJsonString(AppJsonContext.Default.Options));
                updateCommand.Parameters.AddWithValue(
                    "$updatedAt",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                updateCommand.Parameters.AddWithValue("$path", projectPath);
                var changedRows = updateCommand.ExecuteNonQuery();
                ExecuteNonQuery(connection, "COMMIT");
                return changedRows == 1;
            }
            catch
            {
                try
                {
                    ExecuteNonQuery(connection, "ROLLBACK");
                }
                catch
                {
                    // Preserve the original database exception.
                }

                throw;
            }
        }
        catch (SqliteException)
        {
            // Unity Hub treats lock contention as transient and never diverts
            // an authoritative hub.db write into the legacy JSON mirror.
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? TryAddOrUpdateProjectInDatabase(
        string projectPath,
        string title,
        string version,
        bool isFavorite,
        bool? hasCustomDisplayName,
        string? buildTarget)
    {
        if (!File.Exists(HubDatabasePath))
        {
            return null;
        }

        try
        {
            using var connection = OpenHubDatabase(SqliteOpenMode.ReadWrite);
            if (!HasProjectsTable(connection)
                || (!HasProjectsMigrationMarker(connection) && !HasAnyProjects(connection)))
            {
                return null;
            }

            ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
            try
            {
                using var readCommand = connection.CreateCommand();
                readCommand.CommandText = "SELECT data FROM projects WHERE path = $path";
                readCommand.Parameters.AddWithValue("$path", projectPath);
                var storedData = readCommand.ExecuteScalar() as string;
                var lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                JsonObject projectObject;

                if (storedData is not null)
                {
                    projectObject = JsonNode.Parse(storedData)?.AsObject()
                        ?? throw new JsonException("The stored Unity Hub project entry is invalid.");
                    projectObject["title"] = title;
                    projectObject["version"] = version;
                    projectObject["isFavorite"] = isFavorite;
                    if (!string.IsNullOrWhiteSpace(buildTarget))
                    {
                        projectObject["buildTarget"] = buildTarget;
                    }
                    if (hasCustomDisplayName.HasValue)
                    {
                        projectObject["hasCustomDisplayName"] = hasCustomDisplayName.Value;
                    }

                    projectObject["path"] ??= projectPath;
                    projectObject["containingFolderPath"] ??= Path.GetDirectoryName(projectPath) ?? string.Empty;
                    projectObject["lastModified"] ??= lastModified;
                }
                else
                {
                    projectObject = new JsonObject
                    {
                        ["title"] = title,
                        ["path"] = projectPath,
                        ["containingFolderPath"] = Path.GetDirectoryName(projectPath) ?? string.Empty,
                        ["version"] = version,
                        ["lastModified"] = lastModified,
                        ["isFavorite"] = isFavorite,
                        ["isCustomEditor"] = false,
                        ["hasCustomDisplayName"] = hasCustomDisplayName ?? false
                    };
                    if (!string.IsNullOrWhiteSpace(buildTarget))
                    {
                        projectObject["buildTarget"] = buildTarget;
                    }
                }

                using var upsertCommand = connection.CreateCommand();
                upsertCommand.CommandText = """
                    INSERT INTO projects (path, data, updated_at)
                    VALUES ($path, $data, $updatedAt)
                    ON CONFLICT(path) DO UPDATE SET
                        data = excluded.data,
                        updated_at = excluded.updated_at
                    """;
                upsertCommand.Parameters.AddWithValue("$path", projectPath);
                upsertCommand.Parameters.AddWithValue(
                    "$data",
                    projectObject.ToJsonString(AppJsonContext.Default.Options));
                upsertCommand.Parameters.AddWithValue("$updatedAt", lastModified);
                upsertCommand.ExecuteNonQuery();
                ExecuteNonQuery(connection, "COMMIT");
                return true;
            }
            catch
            {
                TryRollback(connection);
                throw;
            }
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool? TryRemoveProjectFromDatabase(string projectPath)
    {
        if (!File.Exists(HubDatabasePath))
        {
            return null;
        }

        try
        {
            using var connection = OpenHubDatabase(SqliteOpenMode.ReadWrite);
            if (!HasProjectsTable(connection)
                || (!HasProjectsMigrationMarker(connection) && !HasAnyProjects(connection)))
            {
                return null;
            }

            ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
            try
            {
                using var deleteCommand = connection.CreateCommand();
                deleteCommand.CommandText = "DELETE FROM projects WHERE path = $path";
                deleteCommand.Parameters.AddWithValue("$path", projectPath);
                deleteCommand.ExecuteNonQuery();
                ExecuteNonQuery(connection, "COMMIT");
                return true;
            }
            catch
            {
                TryRollback(connection);
                throw;
            }
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static void TryRollback(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "ROLLBACK");
        }
        catch
        {
            // Preserve the original database failure.
        }
    }

    private static SqliteConnection OpenHubDatabase(SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = HubDatabasePath,
            Mode = mode,
            DefaultTimeout = 5
        }.ToString());
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000");
        return connection;
    }

    private static bool HasProjectsTable(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'projects'
            """;
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static bool HasAnyProjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM projects LIMIT 1)";
        return Convert.ToInt64(command.ExecuteScalar()) != 0;
    }

    private static bool HasProjectsMigrationMarker(SqliteConnection connection)
    {
        using var tableCommand = connection.CreateCommand();
        tableCommand.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = 'meta'
            """;
        if (Convert.ToInt64(tableCommand.ExecuteScalar()) == 0)
        {
            return false;
        }

        using var markerCommand = connection.CreateCommand();
        markerCommand.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM meta
                WHERE key = 'projects_json_migrated'
            )
            """;
        return Convert.ToInt64(markerCommand.ExecuteScalar()) != 0;
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static string NormalizeProjectTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(tag.Length);
        var pendingSpace = false;
        foreach (var character in tag)
        {
            if (character <= 31 || character == 127 || ReservedProjectTagCharacters.Contains(character))
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        var normalized = builder.ToString().Trim();
        if (normalized.Length > MaximumProjectTagLength)
        {
            normalized = normalized[..MaximumProjectTagLength].Trim();
        }

        return normalized;
    }

    public void UpdateProjectGroup(string projectPath, string group)
    {
        if (!File.Exists(ProjectsJsonPath)) return;

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null || rootNode["data"] is not JsonObject dataObj) return;

            if (dataObj.ContainsKey(projectPath) && dataObj[projectPath] is JsonObject itemObj)
            {
                if (string.IsNullOrWhiteSpace(group) || string.Equals(group, "Ungrouped", StringComparison.OrdinalIgnoreCase))
                {
                    itemObj.Remove("group");
                }
                else
                {
                    itemObj["group"] = group.Trim();
                }

                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void UpdateProjectCommandLineArguments(string projectPath, string commandLineArguments)
    {
        if (!File.Exists(ProjectsJsonPath)) return;

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null || rootNode["data"] is not JsonObject dataObj) return;

            if (dataObj.ContainsKey(projectPath) && dataObj[projectPath] is JsonObject itemObj)
            {
                if (string.IsNullOrWhiteSpace(commandLineArguments))
                {
                    itemObj.Remove("commandLineArguments");
                }
                else
                {
                    itemObj["commandLineArguments"] = commandLineArguments.Trim();
                }

                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }
        }
        catch
        {
            // Ignore write errors
        }
    }

    public void AddOrUpdateProject(
        string projectPath,
        string title,
        string version,
        bool isFavorite = false,
        bool? hasCustomDisplayName = null,
        string? buildTarget = null)
    {
        var databaseResult = TryAddOrUpdateProjectInDatabase(
            projectPath,
            title,
            version,
            isFavorite,
            hasCustomDisplayName,
            buildTarget);
        if (databaseResult.HasValue)
        {
            if (!databaseResult.Value)
            {
                throw new IOException("Unity Hub's project database could not be updated. Try again after Unity Hub finishes its current operation.");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(ProjectsJsonPath)!);

        JsonObject rootNode;
        if (File.Exists(ProjectsJsonPath))
        {
            try
            {
                var text = ReadAllTextShared(ProjectsJsonPath);
                rootNode = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
            }
            catch
            {
                rootNode = new JsonObject();
            }
        }
        else
        {
            rootNode = new JsonObject();
        }

        if (!rootNode.ContainsKey("schema_version"))
        {
            rootNode["schema_version"] = "v1";
        }

        if (!rootNode.ContainsKey("data") || rootNode["data"] is not JsonObject dataObj)
        {
            dataObj = new JsonObject();
            rootNode["data"] = dataObj;
        }

        long lastModified = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (dataObj.ContainsKey(projectPath) && dataObj[projectPath] is JsonObject existingObj)
        {
            // Modify properties IN-PLACE to preserve all existing Unity Hub metadata
            // (e.g. path, containingFolderPath, architecture, changeset, cloudProjectId, etc.)
            existingObj["title"] = title;
            existingObj["version"] = version;
            existingObj["isFavorite"] = isFavorite;
            if (!string.IsNullOrWhiteSpace(buildTarget))
            {
                existingObj["buildTarget"] = buildTarget;
            }
            if (hasCustomDisplayName.HasValue)
            {
                existingObj["hasCustomDisplayName"] = hasCustomDisplayName.Value;
            }

            if (!existingObj.ContainsKey("path"))
            {
                existingObj["path"] = projectPath;
            }
            if (!existingObj.ContainsKey("containingFolderPath"))
            {
                existingObj["containingFolderPath"] = Path.GetDirectoryName(projectPath) ?? string.Empty;
            }
            if (!existingObj.ContainsKey("lastModified"))
            {
                existingObj["lastModified"] = lastModified;
            }
        }
        else
        {
            // New project entry with all required standard fields
            var containingFolder = Path.GetDirectoryName(projectPath) ?? string.Empty;
            var projectEntry = new JsonObject
            {
                ["title"] = title,
                ["path"] = projectPath,
                ["containingFolderPath"] = containingFolder,
                ["version"] = version,
                ["lastModified"] = lastModified,
                ["isFavorite"] = isFavorite,
                ["isCustomEditor"] = false,
                ["hasCustomDisplayName"] = hasCustomDisplayName ?? false
            };
            if (!string.IsNullOrWhiteSpace(buildTarget))
            {
                projectEntry["buildTarget"] = buildTarget;
            }
            dataObj[projectPath] = projectEntry;
        }

        // Repair any existing broken entries in dataObj (e.g. missing 'path' or 'containingFolderPath')
        foreach (var kvp in dataObj.ToList())
        {
            if (kvp.Value is JsonObject itemObj)
            {
                var pPath = kvp.Key;
                if (!itemObj.ContainsKey("path"))
                {
                    itemObj["path"] = pPath;
                }
                if (!itemObj.ContainsKey("containingFolderPath"))
                {
                    itemObj["containingFolderPath"] = Path.GetDirectoryName(pPath) ?? string.Empty;
                }
            }
        }

        var options = AppJsonContext.Default.Options;
        CreateBackup();
        WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
    }

    public string? SynchronizeProjectVersionFromDisk(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath))
        {
            return null;
        }

        var diskVersion = ParseProjectVersion(projectPath);
        if (string.IsNullOrWhiteSpace(diskVersion)
            || string.Equals(diskVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(ProjectsJsonPath))
        {
            return null;
        }

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode?["data"] is not JsonObject dataObj
                || !dataObj.TryGetPropertyValue(projectPath, out var projectNode)
                || projectNode is not JsonObject projectObject)
            {
                return diskVersion;
            }

            var storedVersion = projectObject["version"] is JsonValue versionNode
                && versionNode.TryGetValue<string>(out var value)
                    ? value
                    : null;
            if (!string.Equals(storedVersion, diskVersion, StringComparison.OrdinalIgnoreCase))
            {
                projectObject["version"] = diskVersion;
                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }

            return diskVersion;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public int ImportProjectsFromDirectory(string projectDirectory)
    {
        if (!Directory.Exists(projectDirectory))
        {
            return 0;
        }

        var importedCount = 0;
        foreach (var directory in Directory.EnumerateDirectories(
                     projectDirectory,
                     "*",
                     SearchOption.TopDirectoryOnly))
        {
            var folderName = Path.GetFileName(directory);
            if (folderName.StartsWith(".", StringComparison.Ordinal)
                || !File.Exists(Path.Combine(
                    directory,
                    "ProjectSettings",
                    "ProjectVersion.txt")))
            {
                continue;
            }

            var version = ParseProjectVersion(directory);
            if (string.Equals(version, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = _projectSettingsService.GetShowProductNames()
                ? ParseProjectProductName(directory) ?? folderName
                : folderName;
            AddOrUpdateProject(directory, title, version);
            importedCount++;
        }

        return importedCount;
    }

    public void RemoveProject(string projectPath)
    {
        var databaseResult = TryRemoveProjectFromDatabase(projectPath);
        if (databaseResult.HasValue)
        {
            if (!databaseResult.Value)
            {
                throw new IOException("Unity Hub's project database could not be updated. Try again after Unity Hub finishes its current operation.");
            }
        }

        if (!File.Exists(ProjectsJsonPath))
        {
            return;
        }

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null)
            {
                return;
            }

            if (rootNode["data"] is JsonObject dataObj && dataObj.ContainsKey(projectPath))
            {
                dataObj.Remove(projectPath);
                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }
        }
        catch
        {
            // Silently fail — the project just stays in the list
        }
    }

    public int RemoveMissingProjects()
    {
        if (!File.Exists(ProjectsJsonPath))
        {
            return 0;
        }

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null || rootNode["data"] is not JsonObject dataObj)
            {
                return 0;
            }

            int removedCount = 0;
            var keysToRemove = new List<string>();

            foreach (var kvp in dataObj)
            {
                var projectPath = kvp.Key;
                if (!Directory.Exists(projectPath))
                {
                    keysToRemove.Add(projectPath);
                }
            }

            foreach (var key in keysToRemove)
            {
                dataObj.Remove(key);
                removedCount++;
            }

            if (removedCount > 0)
            {
                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }

            return removedCount;
        }
        catch
        {
            return 0;
        }
    }

    public bool DisconnectProjectFromSourceControl(string projectPath)
        => UpdateProjectConnectionMetadata(projectPath, disconnectCloud: false, disconnectSourceControl: true);

    public bool DisconnectProjectFromCloud(string projectPath, bool disconnectSourceControl)
        => UpdateProjectConnectionMetadata(projectPath, disconnectCloud: true, disconnectSourceControl);

    private bool UpdateProjectConnectionMetadata(
        string projectPath,
        bool disconnectCloud,
        bool disconnectSourceControl)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return false;
        }

        var databaseResult = TryUpdateProjectConnectionMetadataInDatabase(
            projectPath,
            disconnectCloud,
            disconnectSourceControl);
        if (databaseResult == false)
        {
            return false;
        }

        var jsonResult = TryUpdateProjectConnectionMetadataInJson(
            projectPath,
            disconnectCloud,
            disconnectSourceControl);
        return databaseResult == true || jsonResult;
    }

    private static bool? TryUpdateProjectConnectionMetadataInDatabase(
        string projectPath,
        bool disconnectCloud,
        bool disconnectSourceControl)
    {
        if (!File.Exists(HubDatabasePath))
        {
            return null;
        }

        try
        {
            using var connection = OpenHubDatabase(SqliteOpenMode.ReadWrite);
            if (!HasProjectsTable(connection))
            {
                return null;
            }

            ExecuteNonQuery(connection, "BEGIN IMMEDIATE");
            try
            {
                using var readCommand = connection.CreateCommand();
                readCommand.CommandText = "SELECT data FROM projects WHERE path = $path";
                readCommand.Parameters.AddWithValue("$path", projectPath);
                var storedData = readCommand.ExecuteScalar() as string;
                if (storedData is null)
                {
                    ExecuteNonQuery(connection, "ROLLBACK");
                    return HasProjectsMigrationMarker(connection) || HasAnyProjects(connection)
                        ? false
                        : null;
                }

                var projectObject = JsonNode.Parse(storedData)?.AsObject();
                if (projectObject is null)
                {
                    ExecuteNonQuery(connection, "ROLLBACK");
                    return false;
                }

                ApplyProjectConnectionDisconnect(
                    projectObject,
                    disconnectCloud,
                    disconnectSourceControl);

                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = """
                    UPDATE projects
                    SET data = $data, updated_at = $updatedAt
                    WHERE path = $path
                    """;
                updateCommand.Parameters.AddWithValue(
                    "$data",
                    projectObject.ToJsonString(AppJsonContext.Default.Options));
                updateCommand.Parameters.AddWithValue(
                    "$updatedAt",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                updateCommand.Parameters.AddWithValue("$path", projectPath);
                var changedRows = updateCommand.ExecuteNonQuery();
                ExecuteNonQuery(connection, "COMMIT");
                return changedRows == 1;
            }
            catch
            {
                TryRollback(connection);
                throw;
            }
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool TryUpdateProjectConnectionMetadataInJson(
        string projectPath,
        bool disconnectCloud,
        bool disconnectSourceControl)
    {
        if (!File.Exists(ProjectsJsonPath))
        {
            return false;
        }

        try
        {
            var rootNode = JsonNode.Parse(ReadAllTextShared(ProjectsJsonPath))?.AsObject();
            if (rootNode?["data"] is not JsonObject data
                || data[projectPath] is not JsonObject projectObject)
            {
                return false;
            }

            ApplyProjectConnectionDisconnect(
                projectObject,
                disconnectCloud,
                disconnectSourceControl);
            CreateBackup();
            WriteProjectsJsonAtomically(rootNode.ToJsonString(AppJsonContext.Default.Options));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyProjectConnectionDisconnect(
        JsonObject projectObject,
        bool disconnectCloud,
        bool disconnectSourceControl)
    {
        if (disconnectCloud)
        {
            projectObject.Remove("organizationId");
            projectObject.Remove("cloudProjectId");
            projectObject.Remove("projectName");
            projectObject.Remove("genesisOrgId");
        }

        if (!disconnectSourceControl)
        {
            return;
        }

        projectObject.Remove("vcsProvider");
        projectObject.Remove("vcsConfigurationPath");
        projectObject.Remove("organizationName");
        projectObject.Remove("repositoryName");
        projectObject["vcsDisconnected"] = true;
    }

    public void ConnectProjectToSourceControl(string projectPath, string provider, string orgName, string repoName)
    {
        if (!File.Exists(ProjectsJsonPath)) return;

        try
        {
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null) return;

            if (rootNode["data"] is JsonObject dataObj && dataObj.ContainsKey(projectPath)
                && dataObj[projectPath] is JsonObject existingObj)
            {
                existingObj["vcsProvider"] = provider;
                existingObj["vcsConfigurationPath"] = projectPath;
                existingObj["organizationName"] = orgName;
                existingObj["repositoryName"] = repoName;
                existingObj["vcsDisconnected"] = false;

                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }
        }
        catch
        {
            // Non-critical
        }
    }

    public static string ParseProjectVersion(string projectFolderPath)
    {
        var versionFile = Path.Combine(projectFolderPath, "ProjectSettings", "ProjectVersion.txt");
        if (!File.Exists(versionFile))
        {
            return "Unknown";
        }

        try
        {
            using var stream = new FileStream(
                versionFile,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith("m_EditorVersion:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        return parts[1].Trim();
                    }
                }
            }
        }
        catch (IOException)
        {
            return "Unknown";
        }
        catch (UnauthorizedAccessException)
        {
            return "Unknown";
        }

        return "Unknown";
    }

    public static string? ParseProjectProductName(string projectFolderPath)
    {
        var projectSettingsFile = Path.Combine(
            projectFolderPath,
            "ProjectSettings",
            "ProjectSettings.asset");
        if (!File.Exists(projectSettingsFile))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(projectSettingsFile))
            {
                var trimmedLine = line.TrimStart();
                if (!trimmedLine.StartsWith(
                        "productName:",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmedLine["productName:".Length..].Trim();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch
        {
            // A project being written by Unity should not block the whole list.
        }

        return null;
    }

    private static string? ReadOptionalString(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return property.GetString();
    }

    private static IEnumerable<string> ReadOptionalStringArray(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => item is not null)
            .Select(static item => item!);
    }

    private static bool ReadOptionalBoolean(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.True && property.ValueKind != JsonValueKind.False)
        {
            return false;
        }

        return property.GetBoolean();
    }

    private static long ReadOptionalInt64(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var property))
        {
            return 0;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetInt64(out var numericValue) => numericValue,
            JsonValueKind.String when long.TryParse(property.GetString(), out var stringValue) => stringValue,
            _ => 0
        };
    }

    private void RepairProjectsJson()
    {
        try
        {
            if (!File.Exists(ProjectsJsonPath)) return;
            var text = ReadAllTextShared(ProjectsJsonPath);
            var rootNode = JsonNode.Parse(text)?.AsObject();
            if (rootNode is null) return;

            bool modified = false;

            if (!rootNode.ContainsKey("schema_version"))
            {
                rootNode["schema_version"] = "v1";
                modified = true;
            }

            if (rootNode["data"] is JsonObject dataObj)
            {
                foreach (var kvp in dataObj.ToList())
                {
                    if (kvp.Value is JsonObject itemObj)
                    {
                        var pPath = kvp.Key;
                        if (!itemObj.ContainsKey("path"))
                        {
                            itemObj["path"] = pPath;
                            modified = true;
                        }
                        if (!itemObj.ContainsKey("containingFolderPath"))
                        {
                            itemObj["containingFolderPath"] = Path.GetDirectoryName(pPath) ?? string.Empty;
                            modified = true;
                        }

                        var diskVersion = ParseProjectVersion(pPath);
                        var storedVersion = itemObj["version"] is JsonValue versionNode
                            && versionNode.TryGetValue<string>(out var value)
                                ? value
                                : null;
                        if (!string.Equals(diskVersion, "Unknown", StringComparison.OrdinalIgnoreCase)
                            && !string.Equals(
                                storedVersion,
                                diskVersion,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            itemObj["version"] = diskVersion;
                            modified = true;
                        }
                    }
                }
            }

            if (modified)
            {
                var options = AppJsonContext.Default.Options;
                CreateBackup();
                WriteProjectsJsonAtomically(rootNode.ToJsonString(options));
            }
        }
        catch
        {
            // Ignore repair failures
        }
    }

    private static void CreateBackup()
    {
        try
        {
            if (File.Exists(ProjectsJsonPath))
            {
                var backupPath = ProjectsJsonPath + ".bak";
                File.Copy(ProjectsJsonPath, backupPath, overwrite: true);
            }
        }
        catch
        {
            // Non-critical safety backup
        }
    }

    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WriteProjectsJsonAtomically(string content)
    {
        var directory = Path.GetDirectoryName(ProjectsJsonPath)
            ?? throw new InvalidOperationException("The Unity Hub data directory is invalid.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(ProjectsJsonPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, ProjectsJsonPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // The shared project list is committed; temp cleanup is best effort.
            }
        }
    }
}
