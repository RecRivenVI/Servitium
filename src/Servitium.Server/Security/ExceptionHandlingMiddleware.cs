using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Servitium.Contracts;
using Servitium.Domain.Common;

namespace Servitium.Server.Security;

internal sealed partial class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The outer HTTP boundary must convert unhandled failures to a stable problem response without exposing secrets.")]
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (DomainException exception)
        {
            DomainRequestFailed(logger, exception.Code, context.TraceIdentifier);
            await WriteProblemOrAbortAsync(context, StatusCodes.Status409Conflict, exception.Code).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            InvalidJson(logger, exception, context.TraceIdentifier);
            await WriteProblemOrAbortAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            InvalidRequest(logger, context.TraceIdentifier);
            await WriteProblemOrAbortAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.ValidationFailed).ConfigureAwait(false);
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            RequestBodyTooLarge(logger, context.TraceIdentifier);
            await WriteProblemOrAbortAsync(context, StatusCodes.Status413PayloadTooLarge, ErrorCodes.FileTooLarge).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            RequestCancelled(logger, context.TraceIdentifier);
        }
        catch (Exception exception)
        {
            UnhandledRequestFailure(logger, exception, context.TraceIdentifier);
            await WriteProblemOrAbortAsync(context, StatusCodes.Status500InternalServerError, ErrorCodes.InternalError).ConfigureAwait(false);
        }
    }

    private async Task WriteProblemOrAbortAsync(HttpContext context, int statusCode, string errorCode)
    {
        // 流式响应发出响应头后不能再改写状态码或 Problem JSON；此时保留原始异常日志并断开不完整响应。
        if (context.Response.HasStarted)
        {
            StreamingResponseFailed(logger, errorCode, context.TraceIdentifier);
            context.Abort();
            return;
        }

        await ProblemResponses.WriteAsync(context, statusCode, errorCode).ConfigureAwait(false);
    }

    [LoggerMessage(2001, LogLevel.Warning, "Domain request failed with {ErrorCode}; correlation {CorrelationId}")]
    private static partial void DomainRequestFailed(ILogger logger, string errorCode, string correlationId);

    [LoggerMessage(2002, LogLevel.Warning, "Request body is invalid JSON; correlation {CorrelationId}")]
    private static partial void InvalidJson(ILogger logger, Exception exception, string correlationId);

    [LoggerMessage(2003, LogLevel.Debug, "Request was cancelled by the caller; correlation {CorrelationId}")]
    private static partial void RequestCancelled(ILogger logger, string correlationId);

    [LoggerMessage(2005, LogLevel.Warning, "Request body exceeded the endpoint limit; correlation {CorrelationId}")]
    private static partial void RequestBodyTooLarge(ILogger logger, string correlationId);

    [LoggerMessage(2004, LogLevel.Error, "Unhandled request failure; correlation {CorrelationId}")]
    private static partial void UnhandledRequestFailure(ILogger logger, Exception exception, string correlationId);

    [LoggerMessage(2006, LogLevel.Warning, "Response stream failed after headers were sent with {ErrorCode}; correlation {CorrelationId}; the connection will be aborted")]
    private static partial void StreamingResponseFailed(ILogger logger, string errorCode, string correlationId);

    [LoggerMessage(2007, LogLevel.Warning, "Request parameters are invalid; correlation {CorrelationId}")]
    private static partial void InvalidRequest(ILogger logger, string correlationId);
}
