using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Infrastructure.Distribution;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Hmcl;

internal static class HmclDistributionEndpoints
{
    internal static void MapHmclDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        _ = app.MapMethods("/hmcl/{slug}/server-manifest.json", [HttpMethods.Get, HttpMethods.Head], ManifestAsync)
            .ProducesFile("application/json")
            .ProducesEmpty(StatusCodes.Status304NotModified)
            .RequireRateLimiting("hmcl-distribution")
            .WithMetadata(new PublicEndpointAttribute());
        _ = app.MapMethods("/hmcl/{slug}/{token}/server-manifest.json", [HttpMethods.Get, HttpMethods.Head], PrivateManifestAsync)
            .ProducesFile("application/json")
            .ProducesEmpty(StatusCodes.Status304NotModified)
            .RequireRateLimiting("hmcl-distribution")
            .WithMetadata(new PublicEndpointAttribute());
        _ = app.MapMethods("/hmcl/{slug}/overrides/{**path}", [HttpMethods.Get, HttpMethods.Head], FileAsync)
            .ProducesFile("application/octet-stream")
            .ProducesFile("application/octet-stream", StatusCodes.Status206PartialContent)
            .ProducesEmpty(StatusCodes.Status304NotModified)
            .RequireRateLimiting("hmcl-distribution")
            .WithMetadata(new PublicEndpointAttribute());
        _ = app.MapMethods("/hmcl/{slug}/{token}/overrides/{**path}", [HttpMethods.Get, HttpMethods.Head], PrivateFileAsync)
            .ProducesFile("application/octet-stream")
            .ProducesFile("application/octet-stream", StatusCodes.Status206PartialContent)
            .ProducesEmpty(StatusCodes.Status304NotModified)
            .RequireRateLimiting("hmcl-distribution")
            .WithMetadata(new PublicEndpointAttribute());
    }

    private static Task ManifestAsync(HttpContext context, string slug, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        return ManifestCoreAsync(context, slug, null, store, clock, cancellationToken);
    }

    private static Task PrivateManifestAsync(HttpContext context, string slug, string token, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        return ManifestCoreAsync(context, slug, token, store, clock, cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do not use weak cryptographic algorithms", Justification = "HMCL requires SHA-1 as the strong HTTP ETag for the manifest response bytes.")]
    private static async Task ManifestCoreAsync(HttpContext context, string slug, string? token, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        HmclDistributionResolution resolution = await store.ResolveActiveAsync(slug, token, cancellationToken).ConfigureAwait(false);
        if (resolution.Status != "ok")
        {
            context.Response.StatusCode = resolution.Status == "forbidden" ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound;
            await RecordRejectedAsync(store, resolution, clock, manifest: true, context.Response.StatusCode, context).ConfigureAwait(false);
            return;
        }

        StoredHmclChannel channel = resolution.Channel!;
        StoredHmclRelease release = resolution.Release!;
        string tokenSegment = token is null ? string.Empty : "/" + token;
        string fileApi = $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/hmcl/{channel.Slug}{tokenSegment}";
        byte[] manifestBytes = HmclDistributionStore.RenderManifest(release, fileApi);
        string etag = '"' + Convert.ToHexStringLower(SHA1.HashData(manifestBytes)) + '"';
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.LastModified = (release.PublishedAt ?? release.CreatedAt).ToUniversalTime().ToString("R", CultureInfo.InvariantCulture);
        if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, true, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = manifestBytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
        }

        await store.RecordAccessAsync(
            channel.Id, release.Id, clock.UtcNow, true, context.Response.StatusCode,
            HttpMethods.IsHead(context.Request.Method) ? 0 : manifestBytes.Length,
            context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
    }

    private static Task FileAsync(HttpContext context, string slug, string path, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        return FileCoreAsync(context, slug, null, path, store, clock, cancellationToken);
    }

    private static Task PrivateFileAsync(HttpContext context, string slug, string token, string path, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        return FileCoreAsync(context, slug, token, path, store, clock, cancellationToken);
    }

    private static async Task FileCoreAsync(HttpContext context, string slug, string? token, string path, HmclDistributionStore store, IClock clock, CancellationToken cancellationToken)
    {
        if (!IsSafeReleasePath(path))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        HmclDistributionResolution resolution = await store.ResolveActiveAsync(slug, token, cancellationToken).ConfigureAwait(false);
        if (resolution.Status != "ok")
        {
            context.Response.StatusCode = resolution.Status == "forbidden" ? StatusCodes.Status403Forbidden : StatusCodes.Status404NotFound;
            await RecordRejectedAsync(store, resolution, clock, manifest: false, context.Response.StatusCode, context).ConfigureAwait(false);
            return;
        }

        StoredHmclChannel channel = resolution.Channel!;
        StoredHmclRelease release = resolution.Release!;
        StoredHmclReleaseFile? file = await store.FindDistributedFileAsync(
            channel.Id,
            release.Id,
            path,
            cancellationToken).ConfigureAwait(false);
        if (file is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string objectPath = store.GetContentPath(file.Sha1);
        if (!File.Exists(objectPath) || new FileInfo(objectPath).Length != file.Size)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        string etag = '"' + file.Sha1 + '"';
        context.Response.Headers.CacheControl = "no-cache, no-transform";
        context.Response.Headers.ETag = etag;
        context.Response.Headers.AcceptRanges = "bytes";
        context.Response.ContentType = "application/octet-stream";
        if (context.Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
        {
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        ByteRange? range = ParseRange(context.Request.Headers.Range.ToString(), file.Size);
        if (range is not null && !string.IsNullOrWhiteSpace(context.Request.Headers.IfRange) &&
            !string.Equals(context.Request.Headers.IfRange.ToString(), etag, StringComparison.Ordinal))
        {
            range = null;
        }
        if (range is { Valid: false })
        {
            context.Response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
            context.Response.Headers.ContentRange = "bytes */" + file.Size.ToString(CultureInfo.InvariantCulture);
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        long offset = range?.Start ?? 0;
        long length = range?.Length ?? file.Size;
        context.Response.StatusCode = range is null ? StatusCodes.Status200OK : StatusCodes.Status206PartialContent;
        context.Response.ContentLength = length;
        if (range is not null)
        {
            context.Response.Headers.ContentRange = string.Create(CultureInfo.InvariantCulture, $"bytes {offset}-{offset + length - 1}/{file.Size}");
        }

        if (HttpMethods.IsHead(context.Request.Method))
        {
            await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, 0, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        using FileStream stream = new(objectPath, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        stream.Position = offset;
        byte[] buffer = new byte[256 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The content object ended before the advertised length.");
            }

            await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            remaining -= read;
        }

        await store.RecordAccessAsync(channel.Id, release.Id, clock.UtcNow, false, context.Response.StatusCode, length, context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task RecordRejectedAsync(
        HmclDistributionStore store,
        HmclDistributionResolution resolution,
        IClock clock,
        bool manifest,
        int statusCode,
        HttpContext context)
    {
        if (resolution.Channel?.ActiveReleaseId is Guid releaseId)
        {
            await store.RecordAccessAsync(
                resolution.Channel.Id, releaseId, clock.UtcNow, manifest, statusCode, 0,
                context.Connection.RemoteIpAddress, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool IsSafeReleasePath(string path)
    {
        return path.Length is > 0 and <= 240 &&
               !path.StartsWith('/') &&
               !path.Contains('\\', StringComparison.Ordinal) &&
               !path.Contains(':', StringComparison.Ordinal) &&
               !path.Any(char.IsControl) &&
               path.Split('/').All(segment => segment is not ("" or "." or ".."));
    }

    internal static ByteRange? ParseRange(string header, long totalLength)
    {
        if (string.IsNullOrWhiteSpace(header) || header.Contains(',', StringComparison.Ordinal))
        {
            return null;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return new ByteRange(0, 0, false);
        }

        string value = header[6..];
        int separator = value.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            return new ByteRange(0, 0, false);
        }

        string first = value[..separator];
        string last = value[(separator + 1)..];
        if (first.Length == 0)
        {
            if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0 || totalLength == 0)
            {
                return new ByteRange(0, 0, false);
            }

            long length = Math.Min(suffix, totalLength);
            return new ByteRange(totalLength - length, length, true);
        }

        if (!long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out long start) || start < 0 || start >= totalLength)
        {
            return new ByteRange(0, 0, false);
        }

        if (last.Length == 0)
        {
            return new ByteRange(start, totalLength - start, true);
        }

        if (!long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out long end) || end < start)
        {
            return new ByteRange(0, 0, false);
        }

        end = Math.Min(end, totalLength - 1);
        return new ByteRange(start, end - start + 1, true);
    }

    internal sealed record ByteRange(long Start, long Length, bool Valid);
}
