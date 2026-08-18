using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Servitium.Application.Identity;
using Servitium.Infrastructure;
using Servitium.Infrastructure.Logging;
using Servitium.Application.Abstractions;
using Servitium.Server.AgentGateway;
using Servitium.Server.Endpoints;
using Servitium.Server.Files;
using Servitium.Server.Hosting;
using Servitium.Server.Hmcl;
using Servitium.Server.Hubs;
using Servitium.Server.Governance;
using Servitium.Server.Maintenance;
using Servitium.Server.Operations;
using Servitium.Server.OpenApi;
using Servitium.Server.Realtime;
using Servitium.Server.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
ServerHostOptions hostOptions = new();
builder.Configuration.GetSection(ServerHostOptions.SectionName).Bind(hostOptions);
string? environmentDataRoot = Environment.GetEnvironmentVariable("SERVITIUM_DATA_ROOT");
if (!string.IsNullOrWhiteSpace(environmentDataRoot))
{
    hostOptions.DataRoot = environmentDataRoot;
}

string? environmentInstallToken = Environment.GetEnvironmentVariable("SERVITIUM_INSTALL_TOKEN");
if (!string.IsNullOrWhiteSpace(environmentInstallToken))
{
    hostOptions.InstallToken = environmentInstallToken;
}

if (!Path.IsPathFullyQualified(hostOptions.DataRoot))
{
    throw new InvalidOperationException("Servitium data root must be an absolute path.");
}

Directory.CreateDirectory(hostOptions.DataRoot);
string databasePath = Path.Combine(hostOptions.DataRoot, "server.db");
PersistedServerHostSettings.Apply(hostOptions, databasePath);
bool explicitUrls = HasExplicitUrls(args);
string configuredUrls = explicitUrls
    ? builder.Configuration["Urls"] ?? throw new InvalidOperationException("The explicit server URL is empty.")
    : BuildConfiguredUrl(hostOptions);
X509Certificate2? serverCertificate = LoadServerCertificate(hostOptions);
ValidateExposure(configuredUrls, hostOptions.TrustedProxyMode, serverCertificate is not null || HasConfiguredKestrelCertificate(builder.Configuration));
ForwardedHeadersOptions? forwardedHeaders = CreateForwardedHeadersOptions(hostOptions);
_ = builder.WebHost.UseUrls(configuredUrls);
_ = builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
{
    https.SslProtocols = SslProtocols.None;
    if (serverCertificate is not null)
    {
        https.ServerCertificate = serverCertificate;
    }
}));

if (hostOptions.AgentGatewayPort is < 0 or > 65535)
{
    throw new InvalidOperationException("The dedicated Agent gateway port is invalid.");
}

if (hostOptions.AgentGatewayPort > 0)
{
    Uri restEndpoint = RequireLoopbackDevelopmentEndpoint(configuredUrls);
    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        options.Listen(IPAddress.Loopback, restEndpoint.Port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        options.ListenLocalhost(hostOptions.AgentGatewayPort, endpoint => endpoint.Protocols = HttpProtocols.Http2);
    });
}

builder.Services.AddWindowsService(options => options.ServiceName = "Servitium Server");
builder.Services.AddSingleton(hostOptions);
builder.Services.AddServitiumInfrastructure(databasePath);
builder.Services.AddSingleton<ILoggerProvider>(provider => new DailyJsonFileLoggerProvider(
    Path.Combine(hostOptions.DataRoot, "logs"),
    "server",
    provider.GetRequiredService<IClock>()));
builder.Services.AddSingleton(new IdentityServiceOptions(
    TimeSpan.FromHours(12),
    TimeSpan.FromDays(7),
    hostOptions.ApiKeysEnabled));
builder.Services.AddSingleton<IdentityApplicationService>();
builder.Services.AddSingleton<RuntimeSettings>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<RuntimeSettings>());
builder.Services.AddSingleton<OidcLoginService>();
builder.Services.AddSingleton<ILocalPipePeerInspector, WindowsLocalPipePeerInspector>();
builder.Services.AddHostedService<LocalAuthenticationPipeService>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddHealthChecks();
builder.Services.AddGrpc(options =>
{
    options.MaxReceiveMessageSize = 1024 * 1024;
    options.MaxSendMessageSize = 1024 * 1024;
});
builder.Services.AddSingleton<AgentConnectionManager>();
builder.Services.AddSingleton<IAgentGateway>(provider => provider.GetRequiredService<AgentConnectionManager>());
builder.Services.AddSingleton<IAgentFileTransferGateway>(provider => provider.GetRequiredService<AgentConnectionManager>());
builder.Services.AddSingleton<IAgentTerminalGateway>(provider => provider.GetRequiredService<AgentConnectionManager>());
builder.Services.AddSingleton<IAgentDistributionGateway>(provider => provider.GetRequiredService<AgentConnectionManager>());
builder.Services.AddSingleton<NodeReconciler>();
builder.Services.AddSingleton<InstanceTelemetryStore>();
builder.Services.AddSingleton<StagedPrismUploadStore>();
builder.Services.AddSingleton<HmclArchiveUploadManager>();
builder.Services.AddSingleton<ServerOperationExecutor>();
builder.Services.AddHostedService(provider => provider.GetRequiredService<ServerOperationExecutor>());
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = ArchiveUploadPolicy.MaximumRequestBodyBytes);
builder.Services.AddAntiforgery();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(CreateRateLimitPartition);
    options.OnRejected = async (rejected, cancellationToken) =>
        await ProblemResponses.WriteAsync(rejected.HttpContext, StatusCodes.Status429TooManyRequests, Servitium.Contracts.ErrorCodes.RateLimited).ConfigureAwait(false);
    _ = options.AddPolicy("hmcl-distribution", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            "hmcl:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                // HMCL 单客户端可能同时发起 64 路请求并重试 5 次；配置下限避免误伤正常客户端。
                PermitLimit = Math.Max(hostOptions.HmclRequestsPerMinute, 640),
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1),
            }));
});
builder.Services.AddSignalR();
builder.Services.AddSingleton<TerminalStreamBroker>();
builder.Services.AddSingleton<FileUploadSessionManager>();
builder.Services.AddSingleton<DashboardLayoutService>();
builder.Services.AddHostedService<UploadSessionCleanupService>();
builder.Services.AddSingleton<DatabaseBackupService>();
builder.Services.AddHostedService<ServerMaintenanceHostedService>();
builder.Services.AddSingleton<RequestTelemetry>();
builder.Services.AddSingleton<ControlPlaneProcessMetrics>();
builder.Services.AddSingleton<IRealtimeEventStream, RealtimeEventStream>();
builder.Services.AddSingleton<AuthorizationChangeCoordinator>();
builder.Services.AddOpenApi(options => options.AddOperationTransformer<ServitiumOpenApiOperationTransformer>());
if (hostOptions.ResponseCompressionEnabled)
{
    _ = builder.Services.AddResponseCompression(options =>
    {
        options.EnableForHttps = true;
        options.Providers.Add<BrotliCompressionProvider>();
        options.Providers.Add<GzipCompressionProvider>();
    });
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        _ = hostOptions.AllowedOrigins.Length > 0
            ? policy.WithOrigins(hostOptions.AllowedOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()
            : policy.SetIsOriginAllowed(_ => false);
    });
});

WebApplication app = builder.Build();
app.Use(next => new ExceptionHandlingMiddleware(
    next,
    app.Services.GetRequiredService<ILogger<ExceptionHandlingMiddleware>>()).InvokeAsync);
if (forwardedHeaders is not null)
{
    _ = app.UseForwardedHeaders(forwardedHeaders);
}

if (hostOptions.PathBase.Length > 0)
{
    _ = app.UsePathBase(hostOptions.PathBase);
}

app.Use(next => new SecurityHeadersMiddleware(next, app.Services.GetRequiredService<RuntimeSettings>()).InvokeAsync);
app.Use(next => new RequestTelemetryMiddleware(next, app.Services.GetRequiredService<RequestTelemetry>()).InvokeAsync);
app.MapWhen(
    context => context.Request.Path.StartsWithSegments("/hmcl", StringComparison.Ordinal),
    branch =>
    {
        _ = branch.UseRouting();
        _ = branch.UseRateLimiter();
        _ = branch.UseEndpoints(endpoints => endpoints.MapHmclDistributionEndpoints());
    });
if (hostOptions.ResponseCompressionEnabled)
{
    _ = app.UseResponseCompression();
}

app.UseRouting();
app.Use(next => new RequestBodySizeLimitMiddleware(next).InvokeAsync);
app.UseCors();
app.UseRateLimiter();
app.Use(next => new ServitiumAuthenticationMiddleware(
    next,
    app.Services.GetRequiredService<IdentityApplicationService>()).InvokeAsync);
app.Use(next => new CsrfProtectionMiddleware(next).InvokeAsync);
app.Use(next => new ServitiumAuthorizationMiddleware(
    next,
    app.Services.GetRequiredService<Servitium.Application.Authorization.IAuthorizationService>(),
    app.Services.GetRequiredService<RuntimeSettings>()).InvokeAsync);
app.Use(next => new IdempotencyMiddleware(
    next,
    app.Services.GetRequiredService<IIdempotencyStore>(),
    app.Services.GetRequiredService<IClock>(),
    app.Services.GetRequiredService<ServerHostOptions>()).InvokeAsync);
app.Use(next => new MutationAuditMiddleware(
    next,
    app.Services.GetRequiredService<IActivityLogger>(),
    app.Services.GetRequiredService<IAuditLogger>(),
    app.Services.GetRequiredService<IGuidFactory>(),
    app.Services.GetRequiredService<IClock>()).InvokeAsync);

app.MapHealthChecks("/health/live").WithMetadata(new PublicEndpointAttribute());
app.MapHealthChecks("/health/ready").WithMetadata(new PublicEndpointAttribute());
if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi("/swagger/{documentName}/swagger.json").WithMetadata(new PublicEndpointAttribute());
}

app.MapIdentityEndpoints();
app.MapNodeEndpoints();
app.MapInstanceEndpoints();
app.MapFileEndpoints();
app.MapConfigurationAndModEndpoints();
app.MapScheduleEndpoints();
app.MapAuditEndpoints();
app.MapSystemManagementEndpoints();
app.MapDashboardEndpoints();
app.MapHmclManagementEndpoints();
app.MapHub<EventsHub>("/hubs/v1/events");
app.MapHub<TerminalHub>("/hubs/v1/terminal");
app.MapGrpcService<AgentControlService>().WithMetadata(new PublicEndpointAttribute());

string pidPath = Path.Combine(hostOptions.DataRoot, "pid");
await File.WriteAllTextAsync(
    pidPath,
    Environment.ProcessId.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);

HostLog.Starting(
    app.Logger,
    Environment.ProcessId,
    app.Environment.EnvironmentName,
    hostOptions.DataRoot);
Console.WriteLine(FormattableString.Invariant(
    $"Servitium.Server PID={Environment.ProcessId} Env={app.Environment.EnvironmentName} Data={hostOptions.DataRoot} Listen={string.Join(',', app.Urls)}"));

await app.RunAsync().ConfigureAwait(false);

static void ValidateExposure(string? configuredUrls, bool trustedProxyMode, bool trustedCertificateConfigured)
{
    string urls = string.IsNullOrWhiteSpace(configuredUrls) ? "http://127.0.0.1:5344" : configuredUrls;
    foreach (string configuredUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (!Uri.TryCreate(configuredUrl.Replace("*", "0.0.0.0", StringComparison.Ordinal), UriKind.Absolute, out Uri? uri))
        {
            throw new InvalidOperationException("The configured server URL is invalid.");
        }

        bool loopback = string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                        (IPAddress.TryParse(uri.Host, out IPAddress? address) && IPAddress.IsLoopback(address));
        if (!loopback && !trustedProxyMode &&
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || !trustedCertificateConfigured))
        {
            throw new InvalidOperationException("Non-loopback server exposure requires trusted HTTPS or explicitly configured trusted proxy mode.");
        }
    }
}

static Uri RequireLoopbackDevelopmentEndpoint(string configuredUrls)
{
    string[] values = configuredUrls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    return values.Length != 1 || !Uri.TryCreate(values[0], UriKind.Absolute, out Uri? endpoint) ||
        !string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
        !(string.Equals(endpoint.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
          (IPAddress.TryParse(endpoint.Host, out IPAddress? address) && IPAddress.IsLoopback(address)))
        ? throw new InvalidOperationException("The dedicated clear-text Agent gateway is restricted to a single loopback HTTP development endpoint.")
        : endpoint;
}

static ForwardedHeadersOptions? CreateForwardedHeadersOptions(ServerHostOptions options)
{
    if (!options.TrustedProxyMode)
    {
        return null;
    }

    if (options.TrustedProxyAddresses.Length == 0 && options.TrustedProxyNetworks.Length == 0)
    {
        throw new InvalidOperationException("Trusted proxy mode requires at least one explicit proxy address.");
    }

    ForwardedHeadersOptions forwarded = new()
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        ForwardLimit = 1,
        RequireHeaderSymmetry = true,
        ForwardedForHeaderName = options.ForwardedForHeader,
    };
    forwarded.KnownProxies.Clear();
    forwarded.KnownIPNetworks.Clear();
    foreach (string value in options.TrustedProxyAddresses)
    {
        if (!IPAddress.TryParse(value, out IPAddress? address))
        {
            throw new InvalidOperationException("A configured trusted proxy address is invalid.");
        }

        forwarded.KnownProxies.Add(address);
    }

    foreach (string value in options.TrustedProxyNetworks)
    {
        if (!System.Net.IPNetwork.TryParse(value, out System.Net.IPNetwork network))
        {
            throw new InvalidOperationException("A configured trusted proxy network is invalid.");
        }

        forwarded.KnownIPNetworks.Add(network);
    }

    return forwarded;
}

static bool HasExplicitUrls(string[] arguments)
{
    return arguments.Any(argument => argument.Equals("--urls", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--urls=", StringComparison.OrdinalIgnoreCase)) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DOTNET_URLS"));
}

static string BuildConfiguredUrl(ServerHostOptions options)
{
    string scheme = options.TlsCertificateSource == "none" ? Uri.UriSchemeHttp : Uri.UriSchemeHttps;
    string host = IPAddress.TryParse(options.ListenAddress, out IPAddress? address) && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
        ? "[" + options.ListenAddress + "]"
        : options.ListenAddress;
    return FormattableString.Invariant($"{scheme}://{host}:{options.ListenPort}");
}

static bool HasConfiguredKestrelCertificate(IConfiguration configuration)
{
    return !string.IsNullOrWhiteSpace(configuration["Kestrel:Certificates:Default:Path"]) ||
        !string.IsNullOrWhiteSpace(configuration["Kestrel:Certificates:Default:Subject"]);
}

static X509Certificate2? LoadServerCertificate(ServerHostOptions options)
{
    if (options.TlsCertificateSource == "none")
    {
        return null;
    }

    X509Certificate2 certificate = TlsCertificateValidator.LoadConfigured(
        options.TlsCertificateSource,
        options.TlsCertificatePath,
        options.TlsCertificatePassword,
        options.TlsCertificateThumbprint);
    TlsCertificateValidationResult validation = TlsCertificateValidator.Validate(certificate, DateTimeOffset.UtcNow);
    if (!validation.Succeeded)
    {
        certificate.Dispose();
        throw new InvalidOperationException("The configured TLS certificate is invalid: " + validation.Summary);
    }

    return certificate;
}

static RateLimitPartition<string> CreateRateLimitPartition(HttpContext context)
{
    string address = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    string path = context.Request.Path.Value ?? string.Empty;
    int permitLimit;
    string partition;
    if (path.StartsWith("/api/v1/auth/login", StringComparison.Ordinal))
    {
        permitLimit = 20;
        partition = "login:" + address;
    }
    else if (path.Contains("/files", StringComparison.Ordinal))
    {
        permitLimit = 600;
        partition = "files:" + address;
    }
    else if (context.Request.Headers.Authorization.ToString() is string authorization && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        permitLimit = 600;
        partition = "api-key:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(authorization[7..])));
    }
    else
    {
        permitLimit = 1200;
        partition = "default:" + address;
    }

    return RateLimitPartition.GetFixedWindowLimiter(
        partition,
        _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = permitLimit,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1),
        });
}
