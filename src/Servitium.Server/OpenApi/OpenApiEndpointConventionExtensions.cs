using System;
using System.Collections.Generic;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Servitium.Server.OpenApi;

/// <summary>集中表达端点的成功 HTTP 语义；统一错误响应由操作转换器补充。</summary>
internal static class OpenApiEndpointConventionExtensions
{
    internal static RouteHandlerBuilder ProducesJson<TResponse>(this RouteHandlerBuilder builder, int statusCode = 200)
    {
        return builder.Produces<TResponse>(statusCode, "application/json");
    }

    internal static RouteHandlerBuilder ProducesEmpty(this RouteHandlerBuilder builder, int statusCode = 204)
    {
        return builder.Produces(statusCode);
    }

    internal static RouteHandlerBuilder ProducesFile(
        this RouteHandlerBuilder builder,
        string contentType,
        int statusCode = 200)
    {
        return builder
            .Produces(statusCode)
            .WithMetadata(new FileResponseMetadata(statusCode, contentType));
    }

    internal static RouteHandlerBuilder AcceptsMultipart<TRequest>(this RouteHandlerBuilder builder)
    {
        return builder.WithMetadata(new RequestBodyMetadata("multipart/form-data", [typeof(TRequest)], false));
    }

    internal static RouteHandlerBuilder AcceptsBinary(this RouteHandlerBuilder builder)
    {
        return builder.WithMetadata(new RequestBodyMetadata("application/octet-stream", [], true));
    }

    internal static RouteHandlerBuilder AcceptsJsonAlternatives(this RouteHandlerBuilder builder, params Type[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        return types.Length < 2
            ? throw new ArgumentException("At least two JSON request alternatives are required.", nameof(types))
            : builder.WithMetadata(new RequestBodyMetadata("application/json", types, false));
    }
}

internal sealed record FileResponseMetadata(int StatusCode, string ContentType);

internal sealed record RequestBodyMetadata(string ContentType, IReadOnlyList<Type> Types, bool Binary);
