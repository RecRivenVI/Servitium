using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Agent.Archives;
using Servitium.Agent.ControlPlane;
using Servitium.Agent.Distribution;
using Servitium.Agent.Configurations;
using Servitium.Agent.Files;
using Servitium.Agent.Hosting;
using Servitium.Agent.Instances;
using Servitium.Agent.Java;
using Servitium.Agent.Lifecycle;
using Servitium.Agent.Mods;
using Servitium.Agent.Metrics;
using Servitium.Agent.Paths;
using Servitium.Agent.Scheduling;
using Servitium.Agent.Security;
using Servitium.Agent.Storage;
using Servitium.Agent.Terminals;
using Servitium.Application.Abstractions;
using Servitium.Infrastructure.Persistence;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
AgentHostOptions hostOptions = new();
builder.Configuration.GetSection(AgentHostOptions.SectionName).Bind(hostOptions);
hostOptions.Server = builder.Configuration["server"] ?? hostOptions.Server;
hostOptions.RegistrationToken = builder.Configuration["register-token"] ?? hostOptions.RegistrationToken;
hostOptions.NodeName = builder.Configuration["node-name"] ?? hostOptions.NodeName;
hostOptions.ServerCertificateSha256 = builder.Configuration["certificate-sha256"] ?? hostOptions.ServerCertificateSha256;
string? environmentDataRoot = Environment.GetEnvironmentVariable("SERVITIUM_DATA_ROOT");
if (!string.IsNullOrWhiteSpace(environmentDataRoot))
{
    hostOptions.DataRoot = environmentDataRoot;
}

if (!Path.IsPathFullyQualified(hostOptions.DataRoot))
{
    throw new InvalidOperationException("Servitium Agent data root must be an absolute path.");
}

int? credentialCommandExitCode = await RunCredentialCommand.TryExecuteAsync(args, hostOptions, CancellationToken.None).ConfigureAwait(false);
if (credentialCommandExitCode is int commandExitCode)
{
    Environment.ExitCode = commandExitCode;
    return;
}

Directory.CreateDirectory(hostOptions.DataRoot);
string databasePath = Path.Combine(hostOptions.DataRoot, "agent.db");
AgentStorageOptions storageOptions = new(
    hostOptions.DataRoot,
    databasePath,
    Path.Combine(hostOptions.DataRoot, "instances"),
    Path.Combine(hostOptions.DataRoot, "staging"),
    Path.Combine(hostOptions.DataRoot, "java"));
Directory.CreateDirectory(storageOptions.ManagedInstanceRoot);
Directory.CreateDirectory(storageOptions.StagingRoot);
Directory.CreateDirectory(storageOptions.JavaRoot);
SqliteConnectionStringBuilder connection = new()
{
    DataSource = databasePath,
    Mode = SqliteOpenMode.ReadWriteCreate,
    Cache = SqliteCacheMode.Shared,
    ForeignKeys = true,
    Pooling = false,
    DefaultTimeout = 5,
};

builder.Services.AddWindowsService(options => options.ServiceName = "Servitium Agent");
builder.Services.AddSingleton(hostOptions);
builder.Services.AddSingleton(storageOptions);
builder.Services.AddPooledDbContextFactory<AgentDbContext>(options =>
    options.UseSqlite(connection.ToString(), sqlite => sqlite.MigrationsAssembly(typeof(AgentWorker).Assembly.FullName)));
builder.Services.AddSingleton<SqliteWriteCoordinator>();
builder.Services.AddSingleton<IWindowsPathPolicy, WindowsPathPolicy>();
builder.Services.AddSingleton<IHostAddressResolver, SystemHostAddressResolver>();
builder.Services.AddSingleton<AllowedAuthoritySet>();
builder.Services.AddSingleton(provider => new RestrictedHttpOptions(
    provider.GetRequiredService<AllowedAuthoritySet>(),
    8L * 1024 * 1024 * 1024,
    TimeSpan.FromMinutes(10),
    5));
builder.Services.AddSingleton<IOutboundHttpClient, RestrictedOutboundHttpClient>();
builder.Services.AddSingleton(ArchiveSafetyLimits.Default);
builder.Services.AddSingleton<SafeZipExtractor>();
builder.Services.AddSingleton<SafeTarGzipExtractor>();
builder.Services.AddSingleton<InstanceRegistry>();
builder.Services.AddSingleton<JavaRuntimeRegistry>();
builder.Services.AddSingleton<InstanceLifecycleService>();
builder.Services.AddSingleton<AgentFileTaskCoordinator>();
builder.Services.AddSingleton<InstanceFileService>();
builder.Services.AddSingleton<AgentUploadSessionService>();
builder.Services.AddSingleton<InstanceConfigurationService>();
builder.Services.AddSingleton<InstanceModService>();
builder.Services.AddSingleton<MinecraftServerProtocolClient>();
builder.Services.AddSingleton<RconClient>();
builder.Services.AddSingleton<LocalScheduler>();
builder.Services.AddSingleton<IClock>(new Servitium.Infrastructure.Security.SystemClock());
builder.Services.AddSingleton<ILoggerProvider>(provider => new DailyJsonFileLoggerProvider(
    Path.Combine(hostOptions.DataRoot, "logs"),
    "agent",
    provider.GetRequiredService<IClock>()));
builder.Services.AddSingleton<IGuidFactory>(new Servitium.Infrastructure.Security.GuidFactory());
builder.Services.AddSingleton<ISecretProtector, Servitium.Infrastructure.Security.DpapiSecretProtector>();
builder.Services.AddSingleton<AgentCredentialStore>();
builder.Services.AddSingleton<RunCredentialStore>();
builder.Services.AddSingleton<AgentNodeConfigurationStore>();
builder.Services.AddSingleton<AgentTransferRateLimiters>();
builder.Services.AddSingleton<NodeMetricSampler>();
builder.Services.AddSingleton<InboundFileTransferStore>();
builder.Services.AddSingleton<OutboundFileTransferManager>();
builder.Services.AddSingleton<AgentHmclDistributionService>();
builder.Services.AddSingleton<AgentOutboxStore>();
builder.Services.AddSingleton<AgentTerminalStreamManager>();
builder.Services.AddSingleton<AgentCommandDispatcher>();
builder.Services.AddHostedService<AgentDatabaseHostedService>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentFileTaskCoordinator>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentUploadSessionService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<AgentNodeConfigurationStore>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<InstanceLifecycleService>());
builder.Services.AddHostedService(provider => provider.GetRequiredService<LocalScheduler>());
builder.Services.AddHostedService<InstanceTelemetryPublisher>();
builder.Services.AddHostedService<AgentWorker>();

using IHost host = builder.Build();
string pidPath = Path.Combine(hostOptions.DataRoot, "pid");
await File.WriteAllTextAsync(
    pidPath,
    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Servitium.Agent");
HostLog.Starting(
    logger,
    Environment.ProcessId,
    builder.Environment.EnvironmentName,
    hostOptions.DataRoot);
Console.WriteLine(FormattableString.Invariant(
    $"Servitium.Agent PID={Environment.ProcessId} Env={builder.Environment.EnvironmentName} Data={hostOptions.DataRoot} Listen=none"));

bool selfTest = args.Contains("--self-test", StringComparer.Ordinal);
if (selfTest)
{
    await host.StartAsync().ConfigureAwait(false);
    await host.StopAsync().ConfigureAwait(false);
}
else
{
    await host.RunAsync().ConfigureAwait(false);
}
