using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Servitium.Infrastructure.Persistence;

/// <summary>在宿主依赖注入建立前，只读加载会影响监听器的持久化设置。</summary>
public static class PersistedSettingsReader
{
    /// <summary>读取指定设置组；新数据库或尚未迁移时返回空集合。</summary>
    public static IReadOnlyDictionary<string, string?> ReadGroup(string databasePath, string group)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        if (!File.Exists(databasePath))
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        };
        using SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        using SqliteCommand table = connection.CreateCommand();
        table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Settings';";
        if (Convert.ToInt64(table.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
        {
            return new Dictionary<string, string?>(StringComparer.Ordinal);
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ValueJson FROM Settings WHERE Key = $group LIMIT 1;";
        _ = command.Parameters.AddWithValue("$group", group);
        string? json = command.ExecuteScalar() as string;
        return string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string?>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string?>>(json)
                ?? throw new InvalidDataException("The persisted settings group is empty or invalid.");
    }
}
