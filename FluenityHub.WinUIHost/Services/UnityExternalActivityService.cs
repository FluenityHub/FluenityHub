using System.Diagnostics;
using FluenityHub_WinUIHost.Models;
using Microsoft.Data.Sqlite;

namespace FluenityHub_WinUIHost.Services;

internal static class UnityExternalActivityService
{
    public static async Task<IReadOnlyList<UnityExternalActivityInfo>> ReadAsync(CancellationToken cancellationToken, string? databasePath = null)
    {
        var database = databasePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "install-state", "install-state.db");
        if (!File.Exists(database)) return [];
        return await Task.Run(() =>
        {
            var activities = new List<UnityExternalActivityInfo>();
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT version, state, bytes_total, bytes_done, pid, updated_at, error_code
                FROM downloads WHERE state IN ('queued','downloading','failed')
                ORDER BY updated_at DESC LIMIT 12
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var version = reader.GetString(0);
                var state = reader.GetString(1);
                var total = reader.GetInt64(2);
                var done = reader.GetInt64(3);
                var updated = reader.GetInt64(5);
                var status = state switch { "queued" => "Queued", "downloading" => "Downloading", _ => "Failed" };
                if (state != "failed" && IsInterrupted(reader.IsDBNull(4) ? null : reader.GetInt32(4), updated)) status = "Interrupted";
                if (status == "Downloading" && total > 0) status += $" {Math.Clamp(done * 100d / total, 0, 100):0}%";
                if (state == "failed" && !reader.IsDBNull(6)) status += $" ({reader.GetString(6)})";
                activities.Add(new($"Unity {version}", status));
            }
            reader.Close();
            command.CommandText = "SELECT version, phase, pid, updated_at FROM installs LIMIT 1";
            using var installs = command.ExecuteReader();
            if (installs.Read())
            {
                var status = IsInterrupted(installs.IsDBNull(2) ? null : installs.GetInt32(2), installs.GetInt64(3))
                    ? "Interrupted" : installs.GetString(1) == "queued" ? "Waiting to install" : "Installing";
                activities.Insert(0, new($"Unity {installs.GetString(0)}", status));
            }
            return (IReadOnlyList<UnityExternalActivityInfo>)activities;
        }, cancellationToken);
    }

    internal static bool IsInterrupted(int? pid, long updatedAt)
    {
        var updated = updatedAt < 1_000_000_000_000 ? updatedAt * 1000 : updatedAt;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - updated <= 15_000) return false;
        if (pid is null or <= 0) return true;
        try
        {
            using var process = Process.GetProcessById(pid.Value);
            return process.HasExited || new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds() > updated;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}
