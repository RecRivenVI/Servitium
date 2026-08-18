using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-001")]
    [Trait("TestId", "SEC-021")]
    public async Task Server_启动后_健康端点均返回成功()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-server-test-{Guid.NewGuid():N}");
        try
        {
            using WebApplicationFactory<Program> baseFactory = new();
            using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(webHostBuilder =>
                webHostBuilder.UseSetting("Servitium:DataRoot", dataRoot));
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage liveResponse = await client.GetAsync(new Uri("/health/live", UriKind.Relative)).ConfigureAwait(true);
            using HttpResponseMessage readyResponse = await client.GetAsync(new Uri("/health/ready", UriKind.Relative)).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.OK, liveResponse.StatusCode);
            Assert.Equal(HttpStatusCode.OK, readyResponse.StatusCode);
            string policy = liveResponse.Headers.GetValues("Content-Security-Policy").Single();
            Assert.Contains("script-src 'self'", policy, StringComparison.Ordinal);
            Assert.Contains("worker-src 'self' blob:", policy, StringComparison.Ordinal);
            Assert.Contains("frame-src 'none'", policy, StringComparison.Ordinal);
            Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Contract")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "CTR-120")]
    public async Task OpenApi_公开完整且可生成客户端的正式契约()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-openapi-test-{Guid.NewGuid():N}");
        try
        {
            using WebApplicationFactory<Program> baseFactory = new();
            using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(webHostBuilder =>
                webHostBuilder.UseSetting("Servitium:DataRoot", dataRoot));
            using HttpClient client = factory.CreateClient();
            using HttpResponseMessage response = await client.GetAsync(new Uri("/swagger/v1/swagger.json", UriKind.Relative)).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            JsonElement paths = document.RootElement.GetProperty("paths");
            Assert.Equal(145, paths.EnumerateObject().Count());
            string[] requiredPaths =
            [
                "/api/v1/auth/login",
                "/api/v1/nodes",
                "/api/v1/instances/from-archive",
                "/api/v1/instances/attach",
                "/api/v1/imports/prism/preview",
                "/api/v1/hmcl/channels/{channelId}/publish",
                "/api/v1/operations",
            ];
            foreach (string path in requiredPaths)
            {
                Assert.True(paths.TryGetProperty(path, out _), $"Missing OpenAPI path: {path}");
            }

            HashSet<string> operationIds = new(StringComparer.Ordinal);
            int operationCount = 0;
            string[] methods = ["get", "put", "post", "delete", "options", "head", "patch", "trace"];
            foreach (JsonProperty path in paths.EnumerateObject())
            {
                foreach (JsonProperty operationProperty in path.Value.EnumerateObject().Where(item => methods.Contains(item.Name, StringComparer.Ordinal)))
                {
                    operationCount++;
                    JsonElement operation = operationProperty.Value;
                    string operationId = operation.GetProperty("operationId").GetString() ?? string.Empty;
                    Assert.False(string.IsNullOrWhiteSpace(operationId));
                    Assert.True(operationIds.Add(operationId), $"Duplicate operationId: {operationId}");

                    JsonElement responses = operation.GetProperty("responses");
                    Assert.Contains(
                        responses.EnumerateObject(),
                        item => int.TryParse(item.Name, out int status) && status is >= 200 and < 400);

                    JsonElement problem = responses.GetProperty("400")
                        .GetProperty("content")
                        .GetProperty("application/problem+json")
                        .GetProperty("schema")
                        .GetProperty("properties");
                    foreach (string name in new[] { "type", "title", "status", "detail", "instance", "code", "traceId", "parameters", "errors" })
                    {
                        Assert.True(problem.TryGetProperty(name, out _), $"Missing ProblemDetails property '{name}' on {operationProperty.Name.ToUpperInvariant()} {path.Name}");
                    }
                }
            }

            Assert.Equal(181, operationCount);

            JsonElement createInstance = Operation(paths, "/api/v1/instances", "post");
            AssertSchemaReference(createInstance.GetProperty("requestBody").GetProperty("content").GetProperty("application/json").GetProperty("schema"), "CreateInstanceRequest");
            AssertSchemaReference(ResponseSchema(createInstance, "202", "application/json"), "OperationAcceptedDto");

            JsonElement createNodeToken = Operation(paths, "/api/v1/nodes/registration-tokens", "post");
            AssertSchemaReference(ResponseSchema(createNodeToken, "201", "application/json"), "NodeRegistrationTokenResponse");

            JsonElement deleteUser = Operation(paths, "/api/v1/users/{userId}", "delete");
            Assert.False(deleteUser.GetProperty("responses").GetProperty("204").TryGetProperty("content", out _));

            JsonElement downloadFile = Operation(paths, "/api/v1/instances/{instanceId}/files/content", "get");
            JsonElement binarySchema = ResponseSchema(downloadFile, "206", "application/octet-stream");
            Assert.Equal("string", binarySchema.GetProperty("type").GetString());
            Assert.Equal("binary", binarySchema.GetProperty("format").GetString());

            JsonElement prismPreview = Operation(paths, "/api/v1/imports/prism/preview", "post");
            Assert.True(prismPreview.GetProperty("requestBody").GetProperty("content").TryGetProperty("multipart/form-data", out _));
            AssertSchemaReference(ResponseSchema(prismPreview, "201", "application/json"), "StagedPrismPreviewDto");

            JsonElement serverArchive = Operation(paths, "/api/v1/instances/from-archive", "post");
            JsonElement serverArchiveSchema = serverArchive.GetProperty("requestBody").GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema");
            JsonElement serverArchiveProperties = serverArchiveSchema.GetProperty("properties");
            foreach (string property in new[] { "file", "nodeId", "name", "launch", "stopCommand", "stopTimeoutSeconds", "tags", "notes" })
            {
                Assert.True(serverArchiveProperties.TryGetProperty(property, out _), $"Missing server archive field '{property}'.");
            }

            string[] requiredArchiveFields = [.. serverArchiveSchema.GetProperty("required").EnumerateArray().Select(item => item.GetString()!)];
            Assert.DoesNotContain("stopCommand", requiredArchiveFields);
            Assert.DoesNotContain("stopTimeoutSeconds", requiredArchiveFields);
            Assert.DoesNotContain("tags", requiredArchiveFields);
            Assert.DoesNotContain("notes", requiredArchiveFields);

            JsonElement createRelease = Operation(paths, "/api/v1/hmcl/channels/{channelId}/releases", "post");
            JsonElement releaseContent = createRelease.GetProperty("requestBody").GetProperty("content");
            Assert.True(releaseContent.TryGetProperty("multipart/form-data", out _));
            Assert.Equal(2, releaseContent.GetProperty("application/json").GetProperty("schema").GetProperty("oneOf").GetArrayLength());

            JsonElement login = Operation(paths, "/api/v1/auth/login", "post");
            AssertSchemaReference(ResponseSchema(login, "200", "application/json"), "PrincipalDto");
            AssertSchemaReference(ResponseSchema(login, "202", "application/json"), "AuthenticationChallengeDto");
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Category", "Security")]
    [Trait("TestId", "INT-120")]
    public async Task 未认证请求返回Rfc7807媒体类型与稳定字段()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-problem-test-{Guid.NewGuid():N}");
        try
        {
            using WebApplicationFactory<Program> baseFactory = new();
            using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(webHostBuilder =>
                webHostBuilder.UseSetting("Servitium:DataRoot", dataRoot));
            using HttpClient client = factory.CreateClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative)).ConfigureAwait(true);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(true));
            JsonElement root = document.RootElement;
            Assert.Equal("unauthenticated", root.GetProperty("code").GetString());
            Assert.Equal(401, root.GetProperty("status").GetInt32());
            Assert.Equal("/api/v1/auth/me", root.GetProperty("instance").GetString());
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("type").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("title").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("detail").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
            Assert.Equal(JsonValueKind.Object, root.GetProperty("parameters").ValueKind);
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static JsonElement Operation(JsonElement paths, string path, string method)
    {
        return paths.GetProperty(path).GetProperty(method);
    }

    private static JsonElement ResponseSchema(JsonElement operation, string status, string contentType)
    {
        return operation.GetProperty("responses").GetProperty(status).GetProperty("content").GetProperty(contentType).GetProperty("schema");
    }

    private static void AssertSchemaReference(JsonElement schema, string expectedName)
    {
        Assert.EndsWith("/" + expectedName, schema.GetProperty("$ref").GetString(), StringComparison.Ordinal);
    }
}
