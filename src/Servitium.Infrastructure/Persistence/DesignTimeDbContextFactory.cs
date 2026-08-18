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
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string databasePath = Path.Combine(repositoryRoot, "artifacts", "design-time", "server.db");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        DbContextOptionsBuilder<ServitiumDbContext> options = new();
        _ = options.UseSqlite("Data Source=" + databasePath);
        return new ServitiumDbContext(options.Options);
    }
}
