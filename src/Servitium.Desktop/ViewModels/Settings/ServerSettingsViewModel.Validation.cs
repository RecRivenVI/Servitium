using System;
using System.Collections.Generic;
using System.Net;

namespace Servitium.Desktop.ViewModels.Settings;

internal sealed partial class ServerSettingsViewModel
{
    private IEnumerable<string> ValidateRelationships()
    {
        if (_category == "server-security")
        {
            if (!IPAddress.TryParse(ListenAddress.Value, out IPAddress? address)) { yield return Text("desktop.settings-listen-invalid"); }
            else if (!IPAddress.IsLoopback(address) && TlsCertificateSource.Value == "none" && !TrustedProxyMode.IsOn) { yield return Text("desktop.settings-exposure-invalid"); }
            if (SessionAbsoluteHours.Number <= SessionIdleHours.Number) { yield return Text("desktop.settings-session-invalid"); }
            if (TlsCertificateSource.Value == "file" && string.IsNullOrWhiteSpace(TlsCertificatePath.Value) || TlsCertificateSource.Value == "store" && string.IsNullOrWhiteSpace(TlsCertificateThumbprint.Value)) { yield return Text("desktop.settings-tls-incomplete"); }
            if (TrustedProxyMode.IsOn && string.IsNullOrWhiteSpace(TrustedProxyNetworks.Value)) { yield return Text("desktop.settings-proxy-incomplete"); }
        }
        if (_category == "server-identity" && OidcEnabled.IsOn &&
            (!Uri.TryCreate(OidcIssuer.Value, UriKind.Absolute, out Uri? issuer) || issuer.Scheme != "https" || string.IsNullOrWhiteSpace(OidcClientId.Value)))
        { yield return Text("desktop.settings-oidc-incomplete"); }
    }
}
