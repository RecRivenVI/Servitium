using System;
using System.Net.Http;

using Servitium.Client;

namespace Servitium.Desktop.Services;

internal static class DesktopErrorPresentation
{
    internal static string Describe(Exception exception, LocalizationService localization)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(localization);
        string key = exception switch
        {
            ServitiumApiException { Code: "invalid-credentials" } => "desktop.invalid-credentials-next",
            ServitiumApiException { Code: "totp-invalid" } => "desktop.totp-invalid-next",
            ServitiumApiException { Code: "weak-password" } => "desktop.password-policy",
            ServitiumApiException { Code: "eula-not-accepted" } or DesktopOperationException { Code: "eula-not-accepted" } => "desktop.eula-required-next",
            ServitiumApiException { Code: "node-offline" } or DesktopOperationException { Code: "node-offline" } => "desktop.instance-offline-next",
            ServitiumApiException { Code: "file-locked" } or DesktopOperationException { Code: "file-locked" } => "desktop.file-locked-next",
            ServitiumApiException { Code: "instance-not-stopped" } or DesktopOperationException { Code: "instance-not-stopped" } => "desktop.instance-stop-required-next",
            ServitiumApiException { Code: "concurrency-conflict" } => "desktop.config-concurrency-next",
            ServitiumApiException { Code: "forbidden" } => "desktop.permission-error",
            ServitiumApiException { Code: "unauthenticated" } => "desktop.session-expired-next",
            AggregateException => "desktop.transfer-cleanup-failed",
            DesktopOperationException operation => "error." + operation.Code,
            ServitiumApiException api => "error." + api.Code,
            HttpRequestException => "desktop.network-error",
            System.IO.InvalidDataException or System.Text.Json.JsonException => "desktop.protocol-response-error",
            OperationCanceledException => "state.cancelled",
            _ => "desktop.request-error",
        };
        string text = localization.Get(key);
        return text == key ? localization.Get("desktop.request-error") : key.StartsWith("error.", StringComparison.Ordinal) ? text + " " + localization.Get("desktop.retry-next-step") : text;
    }
}
