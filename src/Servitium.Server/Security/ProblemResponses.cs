using System.Collections.Generic;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;

using Servitium.Contracts.Transport;

namespace Servitium.Server.Security;

internal static class ProblemResponses
{
    internal static IResult Create(
        HttpContext context,
        int statusCode,
        string errorCode,
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        return Results.Json(
            CreateBody(context, statusCode, errorCode, parameters, errors),
            statusCode: statusCode,
            contentType: "application/problem+json");
    }

    internal static Task WriteAsync(
        HttpContext context,
        int statusCode,
        string errorCode,
        IReadOnlyDictionary<string, string>? parameters = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors = null)
    {
        context.Response.StatusCode = statusCode;
        ProblemDetailsDto body = CreateBody(context, statusCode, errorCode, parameters, errors);
        return context.Response.WriteAsJsonAsync(
            body,
            options: null,
            contentType: "application/problem+json",
            context.RequestAborted);
    }

    private static ProblemDetailsDto CreateBody(
        HttpContext context,
        int statusCode,
        string errorCode,
        IReadOnlyDictionary<string, string>? parameters,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? errors)
    {
        return new(
            $"https://servitium.invalid/errors/{errorCode}",
            "Request failed",
            statusCode,
            $"The request failed with the stable error code '{errorCode}'.",
            context.Request.Path.Value ?? "/",
            errorCode,
            context.TraceIdentifier,
            parameters ?? new Dictionary<string, string>(),
            errors);
    }
}
