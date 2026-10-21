using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Operations;

namespace Servitium.Desktop.Services;

internal static class DesktopOperationService
{
    internal static async Task<OperationDetailsDto> WaitAsync(ServitiumApiClient client, Guid operationId, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = client.TimeProvider.GetUtcNow().AddMinutes(5);
        while (client.TimeProvider.GetUtcNow() < deadline)
        {
            OperationDetailsDto operation = await client.GetOperationAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (operation.Operation.Status == "Succeeded") { return operation; }
            if (operation.Operation.Status is "Failed" or "Cancelled") { throw new DesktopOperationException(operation.Operation.ErrorCode ?? "operation-failed"); }
            await Task.Delay(TimeSpan.FromMilliseconds(250), client.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
        throw new DesktopOperationException("operation-timeout");
    }
}
