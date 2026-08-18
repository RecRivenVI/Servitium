using System.Linq;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Server.Governance;

namespace Servitium.Server.Security;

internal sealed class SecurityHeadersMiddleware(RequestDelegate next, RuntimeSettings runtimeSettings)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            IHeaderDictionary headers = context.Response.Headers;
            string frameSources = runtimeSettings.EmbedAllowedOrigins.Count == 0
                ? "'none'"
                : string.Join(' ', runtimeSettings.EmbedAllowedOrigins.Select(origin => origin.Replace(";", string.Empty, System.StringComparison.Ordinal)));
            headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; worker-src 'self' blob:; frame-src " + frameSources + "; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            headers.XFrameOptions = "DENY";
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (context.Request.IsHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }
}
