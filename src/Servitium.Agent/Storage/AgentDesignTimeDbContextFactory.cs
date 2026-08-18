using System;
using System.IO;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Servitium.Agent.Storage;

internal sealed class AgentDesignTimeDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args)
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        string databasePath = Path.Combine(repositoryRoot, "artifacts", "design-time", "agent.db");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        return new AgentDbContext(new DbContextOptionsBuilder<AgentDbContext>().UseSqlite("Data Source=" + databasePath).Options);
    }
}
