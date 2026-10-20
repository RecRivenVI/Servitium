using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Servitium.Server.Security;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class UploadLimitTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-166")]
    public void 三个归档端点_声明八GiB文件与Multipart开销硬限制()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-upload-limit-test-{Guid.NewGuid():N}");
        try
        {
            using WebApplicationFactory<Program> baseFactory = new();
            using WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder =>
                builder.UseSetting("Servitium:DataRoot", dataRoot));
            using HttpClient client = factory.CreateClient();
            Endpoint[] endpoints = [.. factory.Services.GetServices<EndpointDataSource>().SelectMany(source => source.Endpoints)];
            string[] routes =
            [
                "/api/v1/instances/from-archive",
                "/api/v1/hmcl/channels/{channelId:guid}/releases",
                "/api/v1/imports/prism/preview",
            ];

            foreach (string route in routes)
            {
                RouteEndpoint endpoint = Assert.Single(
                    endpoints.OfType<RouteEndpoint>(),
                    candidate => string.Equals(candidate.RoutePattern.RawText, route, StringComparison.Ordinal) &&
                        candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Post, StringComparer.Ordinal) == true);
                IRequestSizeLimitMetadata metadata = Assert.IsType<IRequestSizeLimitMetadata>(
                    endpoint.Metadata.GetMetadata<IRequestSizeLimitMetadata>(),
                    exactMatch: false);
                Assert.Equal(ArchiveUploadPolicy.MaximumRequestBodyBytes, metadata.MaxRequestBodySize);
            }

            FormOptions formOptions = factory.Services.GetRequiredService<IOptions<FormOptions>>().Value;
            Assert.Equal(ArchiveUploadPolicy.MaximumRequestBodyBytes, formOptions.MultipartBodyLengthLimit);
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
    [Trait("TestId", "SEC-167")]
    public async Task Kestrel拒绝超限请求_映射为稳定413错误()
    {
        ExceptionHandlingMiddleware middleware = new(
            _ => throw new BadHttpRequestException("Synthetic request limit failure.", StatusCodes.Status413PayloadTooLarge),
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        DefaultHttpContext context = new();
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context).ConfigureAwait(true);

        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using JsonDocument document = await JsonDocument.ParseAsync(context.Response.Body).ConfigureAwait(true);
        Assert.Equal("file-too-large", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "REG-014")]
    public async Task 流式响应已开始后失败_不会二次改写错误响应()
    {
        MemoryStream responseBody = new();
        ExceptionHandlingMiddleware middleware = new(
            async context =>
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                await context.Response.WriteAsync("partial").ConfigureAwait(false);
                throw new IOException("Synthetic stream failure.");
            },
            NullLogger<ExceptionHandlingMiddleware>.Instance);
        FeatureCollection features = new();
        features.Set<IHttpResponseFeature>(new StartedResponseFeature(responseBody));
        features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(responseBody));
        DefaultHttpContext context = new(features);

        await middleware.InvokeAsync(context).ConfigureAwait(true);

        Assert.True(context.Response.HasStarted);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        responseBody.Position = 0;
        using StreamReader reader = new(responseBody, leaveOpen: true);
        Assert.Equal("partial", await reader.ReadToEndAsync().ConfigureAwait(true));
    }

    private sealed class StartedResponseFeature(Stream body) : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = body;
        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
