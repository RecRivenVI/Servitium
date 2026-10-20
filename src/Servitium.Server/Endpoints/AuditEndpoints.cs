using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Servitium.Application.Results;
using Servitium.Application.Authorization;
using Servitium.Contracts.Auditing;
using Servitium.Contracts.Transport;
using Servitium.Domain.Auditing;
using Servitium.Infrastructure.Auditing;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class AuditEndpoints
{
    internal static void MapAuditEndpoints(this WebApplication app)
    {
        RouteGroupBuilder audit = app.MapGroup("/api/v1/audit")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageUsers, ResourceKind.Global));
        _ = audit.MapGet("/activity", ListActivitiesAsync).ProducesJson<CursorPage<ActivityLogDto>>();
        _ = audit.MapGet("/activity/export", ExportActivitiesAsync).ProducesFile("text/csv");
        _ = audit.MapGet("/security", ListAuditAsync).ProducesJson<CursorPage<AuditLogDto>>();
        _ = audit.MapGet("/security/export", ExportAuditAsync).ProducesFile("text/csv");
    }

    private static async Task<IResult> ListActivitiesAsync(
        string? actor,
        string? action,
        string? resource,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        SqliteLogStore reader,
        CancellationToken cancellationToken)
    {
        ActivityLogPage page = await reader.QueryActivitiesAsync(
            new(actor, action, resource, outcome, from, to, DecodeCursor(cursor), Math.Clamp(limit ?? 100, 1, 200)), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = page.Items.Select(item => ToDto(item.Entry)).ToArray(),
            nextCursor = page.NextCursor is long next ? SequenceCursorCodec.Encode(next) : null,
        });
    }

    private static async Task<IResult> ListAuditAsync(
        string? actor,
        string? category,
        string? action,
        string? resource,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? cursor,
        int? limit,
        SqliteLogStore reader,
        CancellationToken cancellationToken)
    {
        AuditLogPage page = await reader.QueryAuditAsync(
            new(actor, category, action, resource, outcome, from, to, DecodeCursor(cursor), Math.Clamp(limit ?? 100, 1, 200)), cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            items = page.Items.Select(item => ToDto(item.Entry)).ToArray(),
            nextCursor = page.NextCursor is long next ? SequenceCursorCodec.Encode(next) : null,
        });
    }

    private static CsvStreamResult ExportActivitiesAsync(
        string? actor,
        string? action,
        string? resource,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        SqliteLogStore reader)
    {
        return new CsvStreamResult(
            (output, cancellationToken) => WriteActivitiesCsvAsync(
                output, new(actor, action, resource, outcome, from, to, null, 500), reader, cancellationToken),
            "servitium-activity.csv");
    }

    private static CsvStreamResult ExportAuditAsync(
        string? actor,
        string? category,
        string? action,
        string? resource,
        string? outcome,
        DateTimeOffset? from,
        DateTimeOffset? to,
        SqliteLogStore reader)
    {
        return new CsvStreamResult(
            (output, cancellationToken) => WriteAuditCsvAsync(
                output, new(actor, category, action, resource, outcome, from, to, null, 500), reader, cancellationToken),
            "servitium-security-audit.csv");
    }

    private static async Task WriteActivitiesCsvAsync(
        Stream output,
        LogQuery query,
        SqliteLogStore reader,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, cancellationToken).ConfigureAwait(false);
        StreamWriter writer = new(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        try
        {
            await writer.WriteAsync("id,actor,action,resource,occurredAt\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            long? cursor = null;
            do
            {
                ActivityLogPage page = await reader.QueryActivitiesAsync(query with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
                foreach (SequencedActivityLog item in page.Items)
                {
                    ActivityLogEntry entry = item.Entry;
                    string line = Csv(entry.Id.ToString("D")) + ',' +
                        Csv(entry.ActorNameSnapshot) + ',' +
                        Csv(entry.Action) + ',' +
                        Csv(entry.Resource) + ',' +
                        Csv(entry.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) + "\r\n";
                    await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task WriteAuditCsvAsync(
        Stream output,
        AuditLogQuery query,
        SqliteLogStore reader,
        CancellationToken cancellationToken)
    {
        await output.WriteAsync(new byte[] { 0xEF, 0xBB, 0xBF }, cancellationToken).ConfigureAwait(false);
        StreamWriter writer = new(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true);
        try
        {
            await writer.WriteAsync("id,category,action,resource,outcome,occurredAt\r\n".AsMemory(), cancellationToken).ConfigureAwait(false);
            long? cursor = null;
            do
            {
                AuditLogPage page = await reader.QueryAuditAsync(query with { Cursor = cursor }, cancellationToken).ConfigureAwait(false);
                foreach (SequencedAuditLog item in page.Items)
                {
                    AuditLogEntry entry = item.Entry;
                    string line = Csv(entry.Id.ToString("D")) + ',' +
                        Csv(entry.Category.ToString()) + ',' +
                        Csv(entry.Action) + ',' +
                        Csv(entry.Resource) + ',' +
                        Csv(entry.OutcomeCode) + ',' +
                        Csv(entry.OccurredAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture)) + "\r\n";
                    await writer.WriteAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                }

                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                cursor = page.NextCursor;
            }
            while (cursor is not null);
        }
        finally
        {
            await writer.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
    }

    private static long? DecodeCursor(string? cursor)
    {
        return string.IsNullOrWhiteSpace(cursor) ? null : SequenceCursorCodec.Decode(cursor);
    }

    private static ActivityLogDto ToDto(ActivityLogEntry entry)
    {
        return new(entry.Id, entry.ActorNameSnapshot, entry.Action, entry.Resource, entry.OccurredAt, entry.OutcomeCode, entry.SourceAddress, entry.Properties);
    }

    private static AuditLogDto ToDto(AuditLogEntry entry)
    {
        return new(entry.Id, entry.Category.ToString(), entry.Action, entry.OutcomeCode, entry.OccurredAt, entry.Resource, entry.ActorNameSnapshot, entry.Properties);
    }

    private sealed class CsvStreamResult(
        Func<Stream, CancellationToken, Task> write,
        string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.ContentType = "text/csv; charset=utf-8";
            httpContext.Response.Headers.ContentDisposition = "attachment; filename=\"" + fileName + "\"";
            await write(httpContext.Response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
