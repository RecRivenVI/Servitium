using System;
using System.Collections.Generic;
using System.Net;

namespace Servitium.Client;

/// <summary>Server 返回稳定错误码时抛出的客户端异常。</summary>
public sealed class ServitiumApiException : Exception
{
    /// <summary>创建空客户端异常。</summary>
    public ServitiumApiException()
    {
    }

    /// <summary>使用消息创建客户端异常。</summary>
    public ServitiumApiException(string message)
        : base(message)
    {
        Code = message;
    }

    /// <summary>使用消息和内部异常创建客户端异常。</summary>
    public ServitiumApiException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = message;
    }

    /// <summary>使用 HTTP 状态和稳定错误码创建客户端异常。</summary>
    public ServitiumApiException(HttpStatusCode statusCode, string code)
        : base(code)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>使用完整 Problem Details 创建客户端异常。</summary>
    public ServitiumApiException(
        HttpStatusCode statusCode,
        string code,
        string? traceId,
        IReadOnlyDictionary<string, string> parameters,
        TimeSpan? retryAfter)
        : base(code)
    {
        StatusCode = statusCode;
        Code = code;
        TraceId = traceId;
        Parameters = parameters;
        RetryAfter = retryAfter;
    }

    /// <summary>HTTP 状态码。</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>稳定错误码。</summary>
    public string Code { get; } = "http-error";

    /// <summary>服务端追踪号。</summary>
    public string? TraceId { get; }

    /// <summary>安全的结构化错误参数。</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>限流响应建议的等待时间。</summary>
    public TimeSpan? RetryAfter { get; }
}
