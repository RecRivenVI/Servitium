using Microsoft.Extensions.Logging;

namespace Servitium.Server.Hosting;

internal static partial class HostLog
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Information,
        Message = "Servitium.Server starting. PID={ProcessId} Environment={EnvironmentName} DataRoot={DataRoot}")]
    internal static partial void Starting(
        ILogger logger,
        int processId,
        string environmentName,
        string dataRoot);
}
