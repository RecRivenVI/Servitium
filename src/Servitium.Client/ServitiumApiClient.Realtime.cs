using System.Collections.Generic;

using Microsoft.AspNetCore.Http.Connections.Client;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    internal void ConfigureHubAuthentication(HttpConnectionOptions options)
    {
        List<string> cookies = [];
        if (SessionToken is string session) { cookies.Add("servitium.session=" + session); }
        if (CsrfToken is string csrf)
        {
            options.Headers["X-CSRF-Token"] = csrf;
            cookies.Add("servitium.csrf=" + csrf);
        }
        if (cookies.Count > 0) { options.Headers["Cookie"] = string.Join("; ", cookies); }
    }
}
