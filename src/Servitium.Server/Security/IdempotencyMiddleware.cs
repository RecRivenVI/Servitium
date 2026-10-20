using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Application.Abstractions;
using Servitium.Contracts;
using Servitium.Server.Hosting;

namespace Servitium.Server.Security;

/// <summary>为认证后的 POST 请求提供持久化幂等响应与请求体冲突检测。</summary>
internal sealed class IdempotencyMiddleware(
    RequestDelegate next,
    IIdempotencyStore store,
    IClock clock,
    ServerHostOptions options)
{
    private const int MaximumStoredEnvelopeBytes = 1024 * 1024;
    private readonly ConcurrentDictionary<string, KeyGate> _gates = new(StringComparer.Ordinal);

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) ||
            !context.Request.Headers.TryGetValue("Idempotency-Key", out Microsoft.Extensions.Primitives.StringValues values) ||
            RequestAuthentication.Get(context) is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (values.Count != 1 || !Guid.TryParseExact(values[0], "D", out Guid suppliedKey))
        {
            await ProblemResponses.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed).ConfigureAwait(false);
            return;
        }

        string storageKey = HashText(FormattableString.Invariant(
            $"{RequestAuthentication.Get(context)!.User.Id:D}\n{context.Request.Method}\n{context.Request.PathBase}{context.Request.Path}\n{suppliedKey:D}"));
        BufferedRequestBody requestBody = await BufferRequestAsync(context, options.DataRoot, context.RequestAborted).ConfigureAwait(false);
        await using ConfiguredAsyncDisposable requestBodyLifetime = requestBody.ConfigureAwait(false);
        string requestHash = HashText(
            context.Request.ContentType + "\n" + context.Request.QueryString.Value + "\n" + requestBody.ContentHash);

        KeyGate gate = RentGate(storageKey);
        await gate.Semaphore.WaitAsync(context.RequestAborted).ConfigureAwait(false);
        try
        {
            IdempotencyRecord? existing = await store.FindAsync(storageKey, context.RequestAborted).ConfigureAwait(false);
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal))
                {
                    await ProblemResponses.WriteAsync(context, StatusCodes.Status409Conflict, ErrorCodes.IdempotencyConflict).ConfigureAwait(false);
                    return;
                }

                context.Response.Headers["Idempotency-Replayed"] = "true";
                await WriteEnvelopeAsync(context, existing.Response, context.RequestAborted).ConfigureAwait(false);
                return;
            }

            Stream originalResponse = context.Response.Body;
            FileStream captured = CreateTemporaryStream(options.DataRoot, ".response");
            await using ConfiguredAsyncDisposable capturedLifetime = captured.ConfigureAwait(false);
            context.Response.Body = captured;
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                context.Response.Body = originalResponse;
            }

            if (context.Response.StatusCode < StatusCodes.Status500InternalServerError &&
                captured.Length <= MaximumStoredEnvelopeBytes)
            {
                captured.Position = 0;
                byte[] body = new byte[captured.Length];
                await captured.ReadExactlyAsync(body, context.RequestAborted).ConfigureAwait(false);
                byte[] envelope = CreateEnvelope(context.Response.StatusCode, context.Response.ContentType, context.Response.Headers.Location, body);
                if (envelope.Length <= MaximumStoredEnvelopeBytes)
                {
                    await store.StoreAsync(
                        new IdempotencyRecord(storageKey, requestHash, envelope, clock.UtcNow.AddHours(24)),
                        context.RequestAborted).ConfigureAwait(false);
                }
            }

            captured.Position = 0;
            await captured.CopyToAsync(originalResponse, context.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            _ = gate.Semaphore.Release();
            ReturnGate(storageKey, gate);
        }
    }

    private static async Task<BufferedRequestBody> BufferRequestAsync(HttpContext context, string dataRoot, CancellationToken cancellationToken)
    {
        FileStream stream = CreateTemporaryStream(dataRoot, ".request");
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                while (true)
                {
                    int read = await context.Request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer, 0, read);
                    await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            context.Request.Body = stream;
            return new BufferedRequestBody(stream, Convert.ToHexString(hash.GetHashAndReset()));
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static FileStream CreateTemporaryStream(string dataRoot, string suffix)
    {
        string directory = Path.Combine(dataRoot, "staging", "idempotency");
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + suffix);
        return new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.DeleteOnClose);
    }

    private static byte[] CreateEnvelope(int statusCode, string? contentType, string? location, byte[] body)
    {
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(statusCode);
            writer.Write(contentType ?? string.Empty);
            writer.Write(location ?? string.Empty);
            writer.Write(body.Length);
            writer.Write(body);
        }

        return stream.ToArray();
    }

    private static async Task WriteEnvelopeAsync(HttpContext context, ReadOnlyMemory<byte> envelope, CancellationToken cancellationToken)
    {
        using MemoryStream stream = new(envelope.ToArray(), writable: false);
        using BinaryReader reader = new(stream, Encoding.UTF8, leaveOpen: false);
        int statusCode = reader.ReadInt32();
        string contentType = reader.ReadString();
        string location = reader.ReadString();
        int length = reader.ReadInt32();
        if (statusCode is < 100 or > 599 || contentType.Length > 256 || location.Length > 2048 || length < 0 || length > MaximumStoredEnvelopeBytes || stream.Length - stream.Position != length)
        {
            throw new InvalidDataException("The stored idempotency response is invalid.");
        }

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = string.IsNullOrEmpty(contentType) ? null : contentType;
        if (!string.IsNullOrEmpty(location))
        {
            context.Response.Headers.Location = location;
        }

        context.Response.ContentLength = length;
        await context.Response.Body.WriteAsync(envelope[^length..], cancellationToken).ConfigureAwait(false);
    }

    private KeyGate RentGate(string key)
    {
        while (true)
        {
            KeyGate gate = _gates.GetOrAdd(key, static _ => new KeyGate());
            _ = Interlocked.Increment(ref gate.References);
            if (_gates.TryGetValue(key, out KeyGate? current) && ReferenceEquals(current, gate))
            {
                return gate;
            }

            _ = Interlocked.Decrement(ref gate.References);
        }
    }

    private void ReturnGate(string key, KeyGate gate)
    {
        if (Interlocked.Decrement(ref gate.References) == 0 &&
            _gates.TryRemove(new KeyValuePair<string, KeyGate>(key, gate)))
        {
            gate.Semaphore.Dispose();
        }
    }

    private static string HashText(string value)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private sealed class KeyGate
    {
        internal SemaphoreSlim Semaphore { get; } = new(1, 1);

        internal int References;
    }

    private sealed class BufferedRequestBody(FileStream stream, string contentHash) : IAsyncDisposable
    {
        internal string ContentHash { get; } = contentHash;

        public ValueTask DisposeAsync()
        {
            return stream.DisposeAsync();
        }
    }
}
