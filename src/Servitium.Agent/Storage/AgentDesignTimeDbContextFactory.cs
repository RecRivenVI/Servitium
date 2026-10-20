using System;
using System.IO;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servitium.Agent.Storage;

internal sealed class AgentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        string directory = args is { Length: > 0 } ? Path.GetFullPath(args[0])
            : throw new ArgumentException("An external design-time directory is required.", nameof(args));
        string databasePath = Path.Combine(directory, "agent.db");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        return new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite("Data Source=" + databasePath).Options);
    }
}
