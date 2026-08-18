using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Domain.Common;
using Servitium.Infrastructure.Networking;

using Xunit;

namespace Servitium.Infrastructure.IntegrationTests;

public sealed class RestrictedOutboundStreamingTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "INT-850")]
    [Trait("TestId", "SEC-193")]
    public async Task 受限出站客户端_响应头后立即返回流并在读取时强制大小上限()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TaskCompletionSource headerSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ServeAsync(listener, headerSent, releaseBody);
        try
        {
            Uri uri = new($"http://synthetic.test:{port}/payload", UriKind.Absolute);
            RestrictedOutboundHttpClient client = new(
                new FixedResolver(IPAddress.Loopback),
                new RestrictedHttpOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { uri.Authority }, 4, TimeSpan.FromSeconds(5), 0));

            Task<OutboundHttpResponse> pending = client.SendAsync(new(uri, "GET", MaximumResponseBytes: 4), CancellationToken.None);
            await headerSent.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            OutboundHttpResponse response = await pending.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            releaseBody.SetResult();
            await using (response.Content.ConfigureAwait(true))
            {
                using MemoryStream destination = new();
                DomainException exception = await Assert.ThrowsAsync<DomainException>(() =>
                    response.Content.CopyToAsync(destination, CancellationToken.None)).ConfigureAwait(true);
                Assert.Equal("url-not-allowed", exception.Code);
            }

            await server.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        finally
        {
            _ = releaseBody.TrySetResult();
            await server.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            listener.Dispose();
        }
    }

    private static async Task ServeAsync(TcpListener listener, TaskCompletionSource headerSent, TaskCompletionSource releaseBody)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        using NetworkStream stream = connection.GetStream();
        byte[] requestBuffer = new byte[4096];
        _ = await stream.ReadAsync(requestBuffer).ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n")).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
        headerSent.SetResult();
        await releaseBody.Task.ConfigureAwait(false);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("12345")).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    private sealed class FixedResolver(IPAddress address) : IHostAddressResolver
    {
        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<IPAddress>>([address]);
        }
    }
}
