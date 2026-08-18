using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

using Servitium.Contracts.Common;

namespace Servitium.Server.OpenApi;

/// <summary>为所有 REST 操作补齐稳定标识和统一 Problem Details 响应。</summary>
internal sealed class ServitiumOpenApiOperationTransformer : IOpenApiOperationTransformer
{
    private static readonly int[] ManagementProblemStatuses =
    [
        400,
        401,
        403,
        404,
        409,
        413,
        416,
        422,
        429,
        500,
        503,
        504,
    ];

    private static readonly int[] DistributionProblemStatuses = [400, 403, 404, 416, 429, 500, 503];

    public async Task TransformAsync(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        string path = "/" + (context.Description.RelativePath ?? string.Empty).TrimStart('/');
        string method = context.Description.HttpMethod ?? "GET";
        operation.OperationId = CreateOperationId(method, path);

        foreach (FileResponseMetadata metadata in context.Description.ActionDescriptor.EndpointMetadata.OfType<FileResponseMetadata>())
        {
            string status = metadata.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            operation.Responses ??= [];
            if (!operation.Responses.TryGetValue(status, out IOpenApiResponse? response) || response is not OpenApiResponse concreteResponse)
            {
                concreteResponse = new OpenApiResponse { Description = "File content." };
                operation.Responses[status] = concreteResponse;
            }

            concreteResponse.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
            concreteResponse.Content[metadata.ContentType] = new()
            {
                Schema = new OpenApiSchema
                {
                    Type = JsonSchemaType.String,
                    Format = "binary",
                },
            };
        }

        foreach (RequestBodyMetadata metadata in context.Description.ActionDescriptor.EndpointMetadata.OfType<RequestBodyMetadata>())
        {
            OpenApiRequestBody requestBody;
            if (operation.RequestBody is OpenApiRequestBody existingRequestBody)
            {
                requestBody = existingRequestBody;
            }
            else
            {
                requestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal),
                };
                operation.RequestBody = requestBody;
            }

            requestBody.Content ??= new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal);
            IOpenApiSchema requestSchema;
            if (metadata.Binary)
            {
                requestSchema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" };
            }
            else
            {
                List<IOpenApiSchema> schemas = [];
                foreach (Type type in metadata.Types)
                {
                    schemas.Add(await context.GetOrCreateSchemaAsync(type, null, cancellationToken).ConfigureAwait(false));
                }

                requestSchema = schemas.Count == 1
                    ? schemas[0]
                    : new OpenApiSchema { OneOf = schemas };
            }

            requestBody.Content[metadata.ContentType] = new() { Schema = requestSchema };
        }

        IOpenApiSchema schema = await context.GetOrCreateSchemaAsync(
            typeof(ProblemDetailsDto),
            null,
            cancellationToken).ConfigureAwait(false);
        int[] statuses = path.StartsWith("/hmcl/", StringComparison.Ordinal)
            ? DistributionProblemStatuses
            : ManagementProblemStatuses;
        foreach (int status in statuses)
        {
            operation.Responses ??= [];
            operation.Responses[status.ToString(System.Globalization.CultureInfo.InvariantCulture)] = new OpenApiResponse
            {
                Description = "RFC 7807 error response with a stable Servitium error code.",
                Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
                {
                    ["application/problem+json"] = new() { Schema = schema },
                },
            };
        }
    }

    private static string CreateOperationId(string method, string path)
    {
        StringBuilder result = new(method.ToUpperInvariant() switch
        {
            "DELETE" => "delete",
            "GET" => "get",
            "HEAD" => "head",
            "PATCH" => "patch",
            "POST" => "post",
            "PUT" => "put",
            _ => "invoke",
        });
        foreach (string segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = segment.Trim('{', '}').TrimStart('*').Split(':', 2)[0];
            foreach (string part in normalized.Split(['-', '.', '_'], StringSplitOptions.RemoveEmptyEntries))
            {
                _ = result.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    _ = result.Append(part.AsSpan(1));
                }
            }
        }

        return result.ToString();
    }
}
