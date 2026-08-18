using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Domain.Common;

namespace Servitium.Infrastructure.Networking;

/// <summary>解析主机地址的可替换边界。</summary>
public interface IHostAddressResolver
{
    /// <summary>解析目标主机的全部当前地址。</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>使用系统 DNS 解析器。</summary>
public sealed class SystemHostAddressResolver : IHostAddressResolver
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>受限出站 HTTP 客户端选项。</summary>
public sealed record RestrictedHttpOptions(
    IReadOnlySet<string> AllowedAuthorities,
    long MaximumResponseBytes,
    TimeSpan Timeout,
    int MaximumRedirects);

/// <summary>运行时可替换的私有来源 authority 白名单。</summary>
public sealed class AllowedAuthoritySet : IReadOnlySet<string>
{
    private readonly ConcurrentDictionary<string, byte> _authorities = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>原子替换白名单。</summary>
    public void Replace(IEnumerable<string> authorities)
    {
        ArgumentNullException.ThrowIfNull(authorities);
        string[] normalized = [.. authorities.Select(value => value.Trim()).Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];
        _authorities.Clear();
        foreach (string authority in normalized)
        {
            if (!Uri.TryCreate("https://" + authority, UriKind.Absolute, out Uri? parsed) || !string.Equals(parsed.Authority, authority, StringComparison.OrdinalIgnoreCase))
            {
                throw new DomainException("validation-failed", "A private source authority is invalid.");
            }

            _authorities[authority] = 0;
        }
    }

    /// <inheritdoc />
    public int Count => _authorities.Count;
    /// <inheritdoc />
    public bool Contains(string item)
    {
        return _authorities.ContainsKey(item);
    }

    /// <inheritdoc />
    public bool IsProperSubsetOf(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).IsProperSubsetOf(other);
    }

    /// <inheritdoc />
    public bool IsProperSupersetOf(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).IsProperSupersetOf(other);
    }

    /// <inheritdoc />
    public bool IsSubsetOf(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).IsSubsetOf(other);
    }

    /// <inheritdoc />
    public bool IsSupersetOf(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).IsSupersetOf(other);
    }

    /// <inheritdoc />
    public bool Overlaps(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).Overlaps(other);
    }

    /// <inheritdoc />
    public bool SetEquals(IEnumerable<string> other)
    {
        return _authorities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(other);
    }

    /// <inheritdoc />
    public IEnumerator<string> GetEnumerator()
    {
        return _authorities.Keys.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }
}

/// <summary>拒绝本机、私网、保留网段和未授权目标，并将连接固定到已验证地址。</summary>
public sealed class RestrictedOutboundHttpClient(
    IHostAddressResolver resolver,
    RestrictedHttpOptions options) : IOutboundHttpClient
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The final response transfers ownership to BoundedOwnedReadStream; redirect and failure paths dispose it locally.")]
    public async Task<OutboundHttpResponse> SendAsync(OutboundHttpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Uri current = request.Uri;
        string method = request.Method;
        ReadOnlyMemory<byte>? body = request.Body;
        for (int redirect = 0; redirect <= options.MaximumRedirects; redirect++)
        {
            ValidateUri(current);
            IReadOnlyList<IPAddress> addresses = await resolver.ResolveAsync(current.DnsSafeHost, cancellationToken).ConfigureAwait(false);
            bool privateAuthorityAllowed = options.AllowedAuthorities.Contains(current.Authority);
            if (addresses.Count == 0 || (!privateAuthorityAllowed && addresses.Any(OutboundAddressPolicy.IsProhibited)))
            {
                throw new DomainException("url-not-allowed", "The outbound target resolved to a prohibited address.");
            }

            PinnedHttpResponse pinned = await SendPinnedAsync(
                current,
                method,
                body,
                request.ContentType,
                addresses[0],
                cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response = pinned.Response;
            if (IsRedirect(response.StatusCode))
            {
                using (pinned)
                {
                    if (redirect == options.MaximumRedirects)
                    {
                        throw new DomainException("redirect-limit-exceeded", "The outbound request exceeded its redirect limit.");
                    }

                    Uri? location = response.Headers.Location ?? throw new DomainException("url-not-allowed", "The redirect response has no target URI.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther)
                    {
                        method = "GET";
                        body = null;
                    }
                }

                continue;
            }

            long maximumResponseBytes = Math.Min(request.MaximumResponseBytes ?? options.MaximumResponseBytes, options.MaximumResponseBytes);
            if (maximumResponseBytes <= 0 || response.Content.Headers.ContentLength > maximumResponseBytes)
            {
                pinned.Dispose();
                throw new DomainException("url-not-allowed", "The outbound response exceeds the configured size limit.");
            }

            try
            {
                Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new OutboundHttpResponse(
                    (int)response.StatusCode,
                    new BoundedOwnedReadStream(responseStream, maximumResponseBytes, options.Timeout, pinned),
                    current,
                    response.Content.Headers.ContentLength);
            }
            catch
            {
                pinned.Dispose();
                throw;
            }
        }

        throw new DomainException("redirect-limit-exceeded", "The outbound request exceeded its redirect limit.");
    }

    private async Task<PinnedHttpResponse> SendPinnedAsync(
        Uri uri,
        string method,
        ReadOnlyMemory<byte>? body,
        string? contentType,
        IPAddress address,
        CancellationToken cancellationToken)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.Online,
                EnabledSslProtocols = SslProtocols.None,
            },
            ConnectCallback = async (context, token) =>
            {
                Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        HttpClient client = new(handler, false);
        try
        {
            using HttpRequestMessage message = new(new HttpMethod(method), uri);
            if (body is { } content)
            {
                message.Content = new ByteArrayContent(content.ToArray());
                if (!string.IsNullOrWhiteSpace(contentType))
                {
                    message.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
                }
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Timeout);
            HttpResponseMessage response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            return new PinnedHttpResponse(response, client, handler);
        }
        catch
        {
            client.Dispose();
            handler.Dispose();
            throw;
        }
    }

    private static void ValidateUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Scheme is not ("http" or "https"))
        {
            throw new DomainException("url-not-allowed", "The outbound URI is not an allowed HTTP target.");
        }

    }

    private static bool IsRedirect(HttpStatusCode statusCode)
    {
        return statusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    }

    private sealed class PinnedHttpResponse(
        HttpResponseMessage response,
        HttpClient client,
        SocketsHttpHandler handler) : IDisposable
    {
        internal HttpResponseMessage Response { get; } = response;

        public void Dispose()
        {
            Response.Dispose();
            client.Dispose();
            handler.Dispose();
        }
    }

    private sealed class BoundedOwnedReadStream(
        Stream inner,
        long maximumBytes,
        TimeSpan readTimeout,
        PinnedHttpResponse owner) : Stream
    {
        private long _remaining = maximumBytes;
        private bool _disposed;

        public override bool CanRead => !_disposed && inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int read = inner.Read(buffer, offset, ProbeCount(count));
            return Account(read);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(readTimeout);
            int read = await inner.ReadAsync(buffer[..ProbeCount(buffer.Length)], timeout.Token).ConfigureAwait(false);
            return Account(read);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
                owner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await inner.DisposeAsync().ConfigureAwait(false);
                owner.Dispose();
            }

            await base.DisposeAsync().ConfigureAwait(false);
            GC.SuppressFinalize(this);
        }

        private int ProbeCount(int requested)
        {
            long available = _remaining == long.MaxValue ? long.MaxValue : _remaining + 1;
            return checked((int)Math.Min(requested, available));
        }

        private int Account(int read)
        {
            if (read > _remaining)
            {
                throw new DomainException("url-not-allowed", "The outbound response exceeds the configured size limit.");
            }

            _remaining -= read;
            return read;
        }
    }
}

/// <summary>出站地址分类策略。</summary>
public static class OutboundAddressPolicy
{
    /// <summary>判断地址是否属于本机、私网、链路本地、文档、组播或其他保留范围。</summary>
    public static bool IsProhibited(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            return IsProhibited(address.MapToIPv4());
        }

        byte[] bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            int first = bytes[0];
            int second = bytes[1];
            return first == 0 ||
                   first == 10 ||
                   first == 127 ||
                   (first == 100 && second is >= 64 and <= 127) ||
                   (first == 169 && second == 254) ||
                   (first == 172 && second is >= 16 and <= 31) ||
                   (first == 192 && second == 0) ||
                   (first == 192 && second == 168) ||
                   (first == 192 && second == 0 && bytes[2] == 2) ||
                   (first == 198 && second is 18 or 19) ||
                   (first == 198 && second == 51 && bytes[2] == 100) ||
                   (first == 203 && second == 0 && bytes[2] == 113) ||
                   first >= 224;
        }

        return address.Equals(IPAddress.IPv6Any) ||
               address.Equals(IPAddress.IPv6Loopback) ||
               (bytes[0] & 0xfe) == 0xfc ||
               (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) ||
               bytes[0] == 0xff ||
               IsDocumentationIpv6(bytes);
    }

    private static bool IsDocumentationIpv6(byte[] bytes)
    {
        return bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8;
    }
}
