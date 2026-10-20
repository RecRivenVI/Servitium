using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Files;
using Servitium.Contracts;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Files;
using Servitium.Contracts.Operations;
using Servitium.Domain.Operations;
using Servitium.Server.AgentGateway;
using Servitium.Server.Files;
using Servitium.Server.Operations;
using Servitium.Server.OpenApi;
using Servitium.Server.Security;

namespace Servitium.Server.Endpoints;

internal static class FileEndpoints
{
    internal static void MapFileEndpoints(this WebApplication app)
    {
        RouteGroupBuilder files = app.MapGroup("/api/v1/instances/{instanceId:guid}/files")
            .WithMetadata(new RequiredAuthorization(ServitiumAction.ManageFiles, ResourceKind.Instance));
        _ = files.MapGet("/", ListAsync).ProducesJson<FileEntryPageDto>();
        _ = files.MapGet("/status", GetStatusAsync).ProducesJson<FileSystemStatusDto>();
        _ = files.MapPost("/directories", CreateDirectoryAsync).ProducesJson<FileEntryDto>();
        _ = files.MapPost("/files", CreateFileAsync).ProducesJson<FileEntryDto>();
        _ = files.MapPost("/rename", RenameAsync).ProducesJson<FileEntryDto>();
        _ = files.MapPost("/copy", CopyAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapPost("/move", MoveAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapPost("/delete", DeleteAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapPost("/compress", CompressAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapPost("/extract", ExtractAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapPost("/download-from-url", DownloadFromUrlAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapGet("/content", DownloadAsync)
            .ProducesFile("application/octet-stream")
            .ProducesFile("application/octet-stream", StatusCodes.Status206PartialContent);
        _ = files.MapPost("/uploads", CreateUploadSessionAsync).ProducesJson<UploadSessionDto>(StatusCodes.Status201Created);
        _ = files.MapGet("/uploads/{uploadId:guid}", GetUploadSessionAsync).ProducesJson<UploadSessionDto>();
        _ = files.MapPut("/uploads/{uploadId:guid}/parts/{index:int}", PutUploadPartAsync).AcceptsBinary().ProducesJson<UploadSessionDto>();
        _ = files.MapPost("/uploads/{uploadId:guid}/complete", CompleteUploadAsync).ProducesJson<OperationAcceptedDto>(StatusCodes.Status202Accepted);
        _ = files.MapDelete("/uploads/{uploadId:guid}", CancelUploadAsync).ProducesEmpty();
        _ = files.MapGet("/text", ReadTextAsync).ProducesJson<TextFileDto>();
        _ = files.MapPut("/text", WriteTextAsync).ProducesJson<TextFileDto>();
    }

    private static Task<IResult> ListAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        string? path,
        string? cursor,
        int pageSize,
        string? search,
        string? sort,
        bool descending,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        return SendAsync<FileEntryPageDto>(
            context,
            nodeId,
            "ListDirectory",
            new AgentListDirectoryCommand(instanceId, path ?? string.Empty, cursor, pageSize == 0 ? 100 : pageSize, search, sort ?? "name", descending),
            gateway,
            cancellationToken);
    }

    private static Task<IResult> GetStatusAsync(HttpContext context, Guid instanceId, Guid nodeId, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<FileSystemStatusDto>(context, nodeId, "GetFileSystemStatus", new AgentInstanceIdCommand(instanceId), gateway, cancellationToken);
    }

    private static Task<IResult> CreateDirectoryAsync(HttpContext context, Guid instanceId, Guid nodeId, FilePathRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<FileEntryDto>(context, nodeId, "CreateDirectory", new AgentFilePathCommand(instanceId, request.Path), gateway, cancellationToken);
    }

    private static Task<IResult> CreateFileAsync(HttpContext context, Guid instanceId, Guid nodeId, FilePathRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<FileEntryDto>(context, nodeId, "CreateFile", new AgentFilePathCommand(instanceId, request.Path), gateway, cancellationToken);
    }

    private static Task<IResult> RenameAsync(HttpContext context, Guid instanceId, Guid nodeId, RenameFileRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendAsync<FileEntryDto>(context, nodeId, "Rename", new AgentRenameFileCommand(instanceId, request.Path, request.NewName), gateway, cancellationToken);
    }

    private static Task<IResult> CopyAsync(HttpContext context, Guid instanceId, Guid nodeId, FileTransferRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.CopyFiles, request.DestinationPath, "Copy", taskId => new AgentFileTransferCommand(instanceId, request.SourcePath, request.DestinationPath, taskId, request.CollisionPolicy), gateway, operations, cancellationToken);
    }

    private static Task<IResult> MoveAsync(HttpContext context, Guid instanceId, Guid nodeId, FileTransferRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.MoveFiles, request.DestinationPath, "Move", taskId => new AgentFileTransferCommand(instanceId, request.SourcePath, request.DestinationPath, taskId, request.CollisionPolicy), gateway, operations, cancellationToken);
    }

    private static Task<IResult> DeleteAsync(HttpContext context, Guid instanceId, Guid nodeId, FileBatchRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        string target = request.Paths.Count == 1 ? request.Paths[0] : request.Paths.Count.ToString(CultureInfo.InvariantCulture) + " paths";
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.DeleteFiles, target, "DeleteBatch", taskId => new AgentFileBatchCommand(instanceId, request.Paths, taskId), gateway, operations, cancellationToken);
    }

    private static Task<IResult> CompressAsync(HttpContext context, Guid instanceId, Guid nodeId, CompressFilesRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.CompressFiles, request.DestinationPath, "Compress", taskId => new AgentCompressCommand(instanceId, request.Paths, request.DestinationPath, request.EntryNameEncoding, taskId), gateway, operations, cancellationToken);
    }

    private static Task<IResult> ExtractAsync(HttpContext context, Guid instanceId, Guid nodeId, ExtractArchiveRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.ExtractArchive, request.DestinationPath, "Extract", taskId => new AgentExtractCommand(instanceId, request.ArchivePath, request.DestinationPath, request.EntryNameEncoding, taskId), gateway, operations, cancellationToken);
    }

    private static Task<IResult> DownloadFromUrlAsync(HttpContext context, Guid instanceId, Guid nodeId, DownloadFromUrlRequest request, IAgentGateway gateway, ServerOperationExecutor operations, CancellationToken cancellationToken)
    {
        return QueueFileOperationAsync(context, nodeId, instanceId, OperationKind.DownloadFileFromUrl, request.DestinationPath, "DownloadFromUrl", taskId => new AgentDownloadFromUrlCommand(instanceId, request.Url, request.DestinationPath, request.Overwrite, taskId), gateway, operations, cancellationToken);
    }

    private static async Task<IResult> DownloadAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        string path,
        IAgentGateway gateway,
        IAgentFileTransferGateway transfers,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new("GetFileDownloadInfo", JsonSerializer.SerializeToUtf8Bytes(new AgentFilePathCommand(instanceId, path))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        FileDownloadInfoDto info = JsonSerializer.Deserialize<FileDownloadInfoDto>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned invalid file download metadata.");
        if (!TryResolveRange(context.Request.Headers.Range, info.Length, out long offset, out long length))
        {
            context.Response.Headers.ContentRange = FormattableString.Invariant($"bytes */{info.Length}");
            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        return new AgentFileDownloadResult(
            transfers,
            nodeId,
            new AgentOpenFileDownloadRequest(instanceId, path, offset, length),
            info.Length,
            Path.GetFileName(info.Path));
    }

    private static bool TryResolveRange(string? header, long totalLength, out long offset, out long length)
    {
        offset = 0;
        length = totalLength;
        if (string.IsNullOrWhiteSpace(header))
        {
            return true;
        }

        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(',', StringComparison.Ordinal))
        {
            return false;
        }

        string range = header[6..].Trim();
        int separator = range.IndexOf('-', StringComparison.Ordinal);
        if (separator < 0)
        {
            return false;
        }

        string startText = range[..separator].Trim();
        string endText = range[(separator + 1)..].Trim();
        if (startText.Length == 0)
        {
            if (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out long suffix) || suffix <= 0 || totalLength == 0)
            {
                return false;
            }

            length = Math.Min(suffix, totalLength);
            offset = totalLength - length;
            return true;
        }

        if (!long.TryParse(startText, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0 || offset >= totalLength)
        {
            return false;
        }

        long end = totalLength - 1;
        if (endText.Length > 0 &&
            (!long.TryParse(endText, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < offset))
        {
            return false;
        }

        end = Math.Min(end, totalLength - 1);
        length = end - offset + 1;
        return true;
    }

    private static async Task<IResult> CreateUploadSessionAsync(
        Guid instanceId,
        Guid nodeId,
        CreateUploadSessionRequest request,
        FileUploadSessionManager uploads,
        CancellationToken cancellationToken)
    {
        UploadSessionDto session = await uploads.CreateAsync(instanceId, nodeId, request, cancellationToken).ConfigureAwait(false);
        return Results.Created($"/api/v1/instances/{instanceId:D}/files/uploads/{session.Id:D}?nodeId={nodeId:D}", session);
    }

    private static async Task<IResult> GetUploadSessionAsync(
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
        FileUploadSessionManager uploads,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await uploads.GetAsync(uploadId, instanceId, nodeId, cancellationToken).ConfigureAwait(false));
    }

    private static async Task<IResult> PutUploadPartAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
        int index,
        string sha256,
        FileUploadSessionManager uploads,
        IAgentFileTransferGateway transfers,
        CancellationToken cancellationToken)
    {
        UploadSessionDto session = await uploads.SavePartAsync(
            uploadId,
            instanceId,
            nodeId,
            index,
            sha256,
            context.Request.Body,
            cancellationToken).ConfigureAwait(false);
        (StoredUploadSession Session, string Path) part = await uploads.GetReceivedPartAsync(uploadId, instanceId, nodeId, index, cancellationToken).ConfigureAwait(false);
        AgentStoreFileUploadPartCommand command = new(
            instanceId,
            uploadId,
            index,
            part.Session.PartCount,
            part.Session.PartSizeBytes,
            part.Session.TotalBytes,
            sha256,
            part.Session.ExpiresAt);
        AgentCommandResult result = await transfers.SendFileCommandAsync(
            nodeId,
            part.Path,
            new("StoreFileUploadPart", JsonSerializer.SerializeToUtf8Bytes(command)),
            cancellationToken).ConfigureAwait(false);
        return !result.Succeeded ? Problem(context, result) : Results.Ok(session);
    }

    private static async Task<IResult> CompleteUploadAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
        CompleteUploadRequest request,
        FileUploadSessionManager uploads,
        IAgentGateway gateway,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        _ = await uploads.GetAsync(uploadId, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            OperationKind.CompleteFileUpload,
            instanceId.ToString("D") + ":" + uploadId.ToString("D"),
            RequestAuthentication.Get(context)!.User.Id,
            async (_, operationToken) =>
            {
                (StoredUploadSession Session, string Path) assembled = await uploads.AssembleAsync(uploadId, instanceId, nodeId, request.Sha256, operationToken).ConfigureAwait(false);
                AgentCompleteChunkedFileUploadCommand command = new(
                    instanceId,
                    uploadId,
                    assembled.Session.DestinationPath,
                    assembled.Session.PartCount,
                    assembled.Session.TotalBytes,
                    request.Sha256,
                    assembled.Session.Overwrite);
                AgentCommandResult result = await gateway.SendAsync(
                    nodeId,
                    new("CompleteChunkedFileUpload", JsonSerializer.SerializeToUtf8Bytes(command)),
                    operationToken).ConfigureAwait(false);
                if (result.Succeeded)
                {
                    await uploads.CancelAsync(uploadId, instanceId, nodeId, CancellationToken.None).ConfigureAwait(false);
                }

                return ServerOperationResult.FromAgent(result);
            },
            null,
            null,
            cancellationToken).ConfigureAwait(false);
        return Results.Accepted("/api/v1/operations/" + accepted.OperationId.ToString("D"), accepted);
    }

    private static async Task<IResult> CancelUploadAsync(
        HttpContext context,
        Guid instanceId,
        Guid nodeId,
        Guid uploadId,
        FileUploadSessionManager uploads,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(
            nodeId,
            new("CancelFileUpload", JsonSerializer.SerializeToUtf8Bytes(new AgentCancelFileUploadCommand(instanceId, uploadId))),
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return ProblemResponses.Create(context, StatusCodes.Status409Conflict, result.ErrorCode ?? ErrorCodes.InternalError);
        }

        await uploads.CancelAsync(uploadId, instanceId, nodeId, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private sealed class AgentFileDownloadResult(
        IAgentFileTransferGateway transfers,
        Guid nodeId,
        AgentOpenFileDownloadRequest request,
        long totalLength,
        string fileName) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            bool partial = request.Offset != 0 || request.Length != totalLength;
            httpContext.Response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
            httpContext.Response.ContentType = "application/octet-stream";
            httpContext.Response.ContentLength = request.Length;
            httpContext.Response.Headers.AcceptRanges = "bytes";
            httpContext.Response.Headers.ContentDisposition = "attachment; filename*=UTF-8''" + Uri.EscapeDataString(fileName);
            if (partial)
            {
                httpContext.Response.Headers.ContentRange = FormattableString.Invariant(
                    $"bytes {request.Offset}-{request.Offset + request.Length - 1}/{totalLength}");
            }

            await transfers.StreamFileAsync(
                nodeId,
                request,
                httpContext.Response.Body,
                httpContext.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Task<IResult> ReadTextAsync(HttpContext context, Guid instanceId, Guid nodeId, string path, string? encoding, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendTextAsync(context, nodeId, "ReadText", new AgentReadTextCommand(instanceId, path, encoding), gateway, cancellationToken);
    }

    private static Task<IResult> WriteTextAsync(HttpContext context, Guid instanceId, Guid nodeId, string path, WriteTextFileRequest request, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        return SendTextAsync(context, nodeId, "WriteText", new AgentWriteTextCommand(instanceId, path, request.Content, request.Encoding), gateway, cancellationToken);
    }

    private static async Task<IResult> SendTextAsync<T>(HttpContext context, Guid nodeId, string type, T command, IAgentGateway gateway, CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        AgentTextFileResponse response = JsonSerializer.Deserialize<AgentTextFileResponse>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid text-file response.");
        return Results.Ok(new TextFileDto(response.RelativePath, response.Content, response.Encoding, response.LineEnding, response.Size));
    }

    private static async Task<IResult> SendAsync<TResult>(
        HttpContext context,
        Guid nodeId,
        string type,
        object command,
        IAgentGateway gateway,
        CancellationToken cancellationToken)
    {
        AgentCommandResult result = await gateway.SendAsync(nodeId, new(type, JsonSerializer.SerializeToUtf8Bytes(command)), cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Problem(context, result);
        }

        TResult response = JsonSerializer.Deserialize<TResult>(result.Payload.Span)
            ?? throw new InvalidOperationException("The Agent returned an invalid file response.");
        return Results.Ok(response);
    }

    private static async Task<IResult> QueueFileOperationAsync(
        HttpContext context,
        Guid nodeId,
        Guid instanceId,
        OperationKind kind,
        string targetPath,
        string commandType,
        Func<Guid, object> commandFactory,
        IAgentGateway gateway,
        ServerOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        Guid initiatedBy = RequestAuthentication.Get(context)!.User.Id;
        OperationAcceptedDto accepted = await operations.EnqueueAsync(
            kind,
            instanceId.ToString("D") + ":" + targetPath,
            initiatedBy,
            async (taskId, operationToken) => ServerOperationResult.FromAgent(await gateway.SendAsync(
                nodeId,
                new AgentCommand(commandType, JsonSerializer.SerializeToUtf8Bytes(commandFactory(taskId))),
                operationToken).ConfigureAwait(false)),
            async (taskId, cancelToken) =>
            {
                AgentCommandResult result = await gateway.SendAsync(
                    nodeId,
                    new AgentCommand("CancelFileTask", JsonSerializer.SerializeToUtf8Bytes(new AgentCancelFileTaskCommand(instanceId, taskId))),
                    cancelToken).ConfigureAwait(false);
                return result.Succeeded;
            },
            null,
            cancellationToken).ConfigureAwait(false);
        string location = "/api/v1/operations/" + accepted.OperationId.ToString("D");
        return Results.Accepted(location, accepted);
    }

    private static IResult Problem(HttpContext context, AgentCommandResult result)
    {
        int status = result.ErrorCode == ErrorCodes.NodeOffline ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
        return ProblemResponses.Create(context, status, result.ErrorCode ?? ErrorCodes.InternalError);
    }
}
