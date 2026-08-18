using System;
using System.IO;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Identity;
using Servitium.Application.Files;
using Servitium.Application.Nodes;
using Servitium.Infrastructure.Identity;
using Servitium.Infrastructure.Maintenance;
using Servitium.Infrastructure.Files;
using Servitium.Infrastructure.Auditing;
using Servitium.Infrastructure.Distribution;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Nodes;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Security;

namespace Servitium.Infrastructure;

/// <summary>注册 Servitium 基础设施实现。</summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册 SQLite 持久化与 Windows 安全原语。</summary>
    public static IServiceCollection AddServitiumInfrastructure(this IServiceCollection services, string databasePath)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string fullPath = Path.GetFullPath(databasePath);
        SqliteConnectionStringBuilder connection = new()
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 5,
        };

        _ = services.AddPooledDbContextFactory<ServitiumDbContext>(options =>
            options.UseSqlite(connection.ToString(), sqlite => sqlite.MigrationsAssembly(typeof(InfrastructureServiceCollectionExtensions).Assembly.FullName)));
        _ = services.AddSingleton(new DatabaseLocation(fullPath));
        _ = services.AddSingleton<SqliteWriteCoordinator>();
        _ = services.AddSingleton<IClock>(new SystemClock());
        _ = services.AddSingleton<IIdentityStore>(provider => new SqliteIdentityStore(
            provider.GetRequiredService<IDbContextFactory<ServitiumDbContext>>(),
            provider.GetRequiredService<SqliteWriteCoordinator>()));
        _ = services.AddSingleton<INodeControlStore>(provider => new SqliteNodeControlStore(
            provider.GetRequiredService<IDbContextFactory<ServitiumDbContext>>(),
            provider.GetRequiredService<SqliteWriteCoordinator>(),
            provider.GetRequiredService<IClock>()));
        _ = services.AddSingleton<IUploadSessionStore>(provider => new SqliteUploadSessionStore(
            provider.GetRequiredService<IDbContextFactory<ServitiumDbContext>>(),
            provider.GetRequiredService<SqliteWriteCoordinator>()));
        _ = services.AddSingleton<HmclDistributionStore>();
        _ = services.AddSingleton<SqliteLogStore>();
        _ = services.AddSingleton<IActivityLogger>(provider => provider.GetRequiredService<SqliteLogStore>());
        _ = services.AddSingleton<IAuditLogger>(provider => provider.GetRequiredService<SqliteLogStore>());
        _ = services.AddSingleton<ILogReader>(provider => provider.GetRequiredService<SqliteLogStore>());
        _ = services.AddSingleton<ControlPlaneStore>();
        _ = services.AddSingleton<IIdempotencyStore, SqliteIdempotencyStore>();
        _ = services.AddSingleton<RetentionMaintenanceService>();
        _ = services.AddSingleton<IOperationTracker>(provider => provider.GetRequiredService<ControlPlaneStore>());
        _ = services.AddSingleton<IGuidFactory>(new GuidFactory());
        _ = services.AddSingleton<IPasswordHasher>(new Argon2idPasswordHasher());
        _ = services.AddSingleton<TotpOptions>();
        _ = services.AddSingleton<ITotpService>(provider => new TotpService(provider.GetRequiredService<TotpOptions>()));
        _ = services.AddSingleton<ISecretProtector>(new DpapiSecretProtector());
        _ = services.AddSingleton<ITokenGenerator>(new SecureTokenGenerator());
        _ = services.AddSingleton<IAuthorizationService, AuthorizationService>();
        _ = services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
        AllowedAuthoritySet allowedAuthorities = new();
        _ = services.AddSingleton(allowedAuthorities);
        _ = services.AddSingleton(new RestrictedHttpOptions(
            allowedAuthorities,
            64L * 1024 * 1024,
            TimeSpan.FromSeconds(30),
            5));
        _ = services.AddSingleton<IOutboundHttpClient, RestrictedOutboundHttpClient>();
        _ = services.AddHostedService<DatabaseMigrationHostedService>();
        _ = services.AddHostedService(provider => provider.GetRequiredService<ControlPlaneStore>());
        return services;
    }
}
