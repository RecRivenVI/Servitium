using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace Servitium.Agent.Storage;

internal sealed class AgentDatabaseHostedService(
    IDbContextFactory<AgentDbContext> contextFactory,
    AgentStorageOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        using AgentDbContext context = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;", cancellationToken).ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;", cancellationToken).ConfigureAwait(false);
        _ = await context.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

internal sealed record AgentStorageOptions(string DataRoot, string DatabasePath, string ManagedInstanceRoot, string StagingRoot, string JavaRoot)
{
    internal AgentStorageOptions(string dataRoot, string databasePath, string managedInstanceRoot, string stagingRoot)
        : this(dataRoot, databasePath, managedInstanceRoot, stagingRoot, Path.Combine(dataRoot, "java"))
    {
    }
}
