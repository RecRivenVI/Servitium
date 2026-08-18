using System;
using System.IO;

namespace Servitium.Server.Hosting;

internal sealed class ServerHostOptions
{
    internal const string SectionName = "Servitium";

    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Servitium.Dev",
        "Server");

    public string? InstallToken { get; set; }

    public bool ApiKeysEnabled { get; set; } = true;

    public bool TrustedProxyMode { get; set; }

    public string[] TrustedProxyAddresses { get; set; } = [];

    public string[] TrustedProxyNetworks { get; set; } = [];

    public string ForwardedForHeader { get; set; } = "X-Forwarded-For";

    public string[] AllowedOrigins { get; set; } = [];

    public bool ResponseCompressionEnabled { get; set; } = true;

    public int HmclRequestsPerMinute { get; set; } = 5000;

    public string PathBase { get; set; } = string.Empty;

    public Uri? PublicBaseUri { get; set; }

    public string ListenAddress { get; set; } = "127.0.0.1";

    public int ListenPort { get; set; } = 5344;

    public string TlsCertificateSource { get; set; } = "none";

    public string? TlsCertificatePath { get; set; }

    public string? TlsCertificatePassword { get; set; }

    public string? TlsCertificateThumbprint { get; set; }

    public int AgentGatewayPort { get; set; }

    public bool LocalAuthenticationEnabled { get; set; } = true;

    public string LocalAuthenticationPipeName { get; set; } = "Servitium.LocalAuth.v1";

    public string InstallDirectory { get; set; } = AppContext.BaseDirectory;
}
