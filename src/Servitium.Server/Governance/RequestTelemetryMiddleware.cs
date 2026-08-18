using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

namespace Servitium.Server.Governance;

internal sealed class RequestTelemetryMiddleware(RequestDelegate next, RequestTelemetry telemetry)
{
    public async Task InvokeAsync(HttpContext context)
    {
        await next(context).ConfigureAwait(false);
        telemetry.Record(context.Response.StatusCode);
    }
}
