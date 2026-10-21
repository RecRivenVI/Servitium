using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Identity;

using Xunit;

namespace Servitium.Client.Tests;

public sealed class TerminalHandshakeTests
{
    [Fact]
    [SuppressMessage("Reliability", "CA2025:Do not pass IDisposable instances into unawaited tasks", Justification = "捕获与协商任务在同一 using 作用域中经 Task.WhenAll 一起等待，且共享有界取消。")]
    public async Task TerminalNegotiationCarriesBothSessionAndCsrfCookiesWithMatchingHeader()
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using AuthenticationHandler handler = new();
        using ServitiumApiClient client = new(new Uri($"http://127.0.0.1:{port}"), handler: handler);
        _ = await client.Session.SignInAsync(new SignInRequest("owner", "synthetic-password"), timeout.Token).ConfigureAwait(true);
        Task<Dictionary<string, string>> capture = CaptureRejectedNegotiationAsync(listener, timeout.Token);
        Task<HttpRequestException> rejected = Assert.ThrowsAsync<HttpRequestException>(async () =>
            await client.Terminal.AttachAsync(Guid.NewGuid(), 80, 24, timeout.Token).ConfigureAwait(true));
        await Task.WhenAll(capture, rejected).ConfigureAwait(true);
        Dictionary<string, string> headers = await capture.ConfigureAwait(true);
        Assert.Equal("synthetic-csrf", headers["X-CSRF-Token"]);
        Assert.Contains("servitium.session=synthetic-session", headers["Cookie"], StringComparison.Ordinal);
        Assert.Contains("servitium.csrf=synthetic-csrf", headers["Cookie"], StringComparison.Ordinal);
    }

    private static async Task<Dictionary<string, string>> CaptureRejectedNegotiationAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        using NetworkStream stream = connection.GetStream();
        using StreamReader reader = new(stream, Encoding.ASCII, false, 1024, true);
        string? first = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        Assert.StartsWith("POST /hubs/v1/terminal/negotiate?", first, StringComparison.Ordinal);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            headers.Add(line[..colon], line[(colon + 1)..].Trim());
        }
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken).ConfigureAwait(false);
        return headers;
    }

    private sealed class AuthenticationHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            bool login = request.RequestUri!.AbsolutePath.EndsWith("/login", StringComparison.Ordinal);
            UserDto user = new(Guid.NewGuid(), "owner", "Synthetic owner", "Owner", false, null, DateTimeOffset.UnixEpoch);
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = login ? JsonContent.Create(new PrincipalDto(user, [], "en-US")) : JsonContent.Create(new AuthenticationStateDto(true, false, null)),
            };
            _ = response.Headers.TryAddWithoutValidation("Set-Cookie", login
                ? "servitium.session=synthetic-session; Secure; HttpOnly; SameSite=Strict"
                : "servitium.csrf=synthetic-csrf; Secure; SameSite=Strict");
            return Task.FromResult(response);
        }
    }
}
