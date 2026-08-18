using System;
using System.IO;

namespace Servitium.Agent.Hosting;

internal sealed class AgentHostOptions
{
    internal const string SectionName = "Servitium";

    public string DataRoot { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Servitium.Dev",
        "Agent");

    public string Server { get; set; } = "http://127.0.0.1:5344";

    public string? RegistrationToken { get; set; }

    public string NodeName { get; set; } = Environment.MachineName;

    public string? ServerCertificateSha256 { get; set; }
}
