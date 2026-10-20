using System;
using System.IO;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servitium.Infrastructure.Persistence;

/// <summary>只为生成受版本控制迁移提供隔离设计时数据库。</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServitiumDbContext>
{
    /// <inheritdoc />
    public ServitiumDbContext CreateDbContext(string[] args)
    {
        string directory = args is { Length: > 0 } ? Path.GetFullPath(args[0])
            : throw new ArgumentException("An external design-time directory is required.", nameof(args));
        string databasePath = Path.Combine(directory, "server.db");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        DbContextOptionsBuilder<ServitiumDbContext> options = new();
        _ = options.UseSqlite("Data Source=" + databasePath);
        return new ServitiumDbContext(options.Options);
    }
}
