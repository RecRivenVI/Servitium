using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Servitium.Contracts.Operations;
using Servitium.Domain.Operations;
using Servitium.Infrastructure.Persistence;
using Servitium.Server.Operations;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class ServerOperationExecutorTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-805")]
    public async Task 长操作_先持久化再后台完成并在提交点前传播取消()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "servitium-operation-executor-" + Guid.NewGuid().ToString("N"));
        WebApplicationFactory<Program> baseFactory = new();
        WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Servitium:DataRoot", dataRoot));
        try
        {
            _ = factory.CreateClient();
            ServerOperationExecutor executor = factory.Services.GetRequiredService<ServerOperationExecutor>();
            ControlPlaneStore store = factory.Services.GetRequiredService<ControlPlaneStore>();
            var userId = Guid.NewGuid();

            OperationAcceptedDto succeeded = await executor.EnqueueAsync(
                OperationKind.CopyFiles,
                "synthetic:copy",
                userId,
                (_, _) => Task.FromResult(new ServerOperationResult(true, /*lang=json,strict*/ "{\"path\":\"copy\"}", null)),
                null,
                null,
                CancellationToken.None).ConfigureAwait(true);
            OperationDetailsDto succeededDetails = await WaitForTerminalAsync(store, succeeded.OperationId).ConfigureAwait(true);
            Assert.Equal("Succeeded", succeededDetails.Operation.Status);
            Assert.Equal(userId, succeededDetails.InitiatedBy);
            Assert.Equal(/*lang=json,strict*/ "{\"path\":\"copy\"}", succeededDetails.Result);
            Assert.False(succeededDetails.Cancellable);

            TaskCompletionSource<bool> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<bool> cancelPropagated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OperationAcceptedDto cancellable = await executor.EnqueueAsync(
                OperationKind.CompressFiles,
                "synthetic:archive",
                userId,
                async (operationId, token) =>
                {
                    _ = started.TrySetResult(true);
                    await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                    return new ServerOperationResult(true, null, null);
                },
                (operationId, cancelToken) =>
                {
                    _ = cancelPropagated.TrySetResult(true);
                    return Task.FromResult(true);
                },
                null,
                CancellationToken.None).ConfigureAwait(true);
            _ = await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            Assert.True(await executor.CancelAsync(cancellable.OperationId, CancellationToken.None).ConfigureAwait(true));
            _ = await cancelPropagated.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            OperationDetailsDto cancelledDetails = await WaitForTerminalAsync(store, cancellable.OperationId).ConfigureAwait(true);
            Assert.Equal("Cancelled", cancelledDetails.Operation.Status);
            Assert.False(cancelledDetails.Cancellable);
            Assert.False(await executor.CancelAsync(cancellable.OperationId, CancellationToken.None).ConfigureAwait(true));
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
            await baseFactory.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-805")]
    public async Task 停机_终结排队操作并回收暂存内容()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), "servitium-operation-stop-" + Guid.NewGuid().ToString("N"));
        WebApplicationFactory<Program> baseFactory = new();
        WebApplicationFactory<Program> factory = baseFactory.WithWebHostBuilder(builder => builder.UseSetting("Servitium:DataRoot", dataRoot));
        try
        {
            _ = factory.CreateClient();
            ServerOperationExecutor executor = factory.Services.GetRequiredService<ServerOperationExecutor>();
            ControlPlaneStore store = factory.Services.GetRequiredService<ControlPlaneStore>();
            TaskCompletionSource<bool>[] started = [.. Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))];

            for (int index = 0; index < started.Length; index++)
            {
                int worker = index;
                _ = await executor.EnqueueAsync(
                    OperationKind.CompressFiles,
                    "synthetic:blocker:" + worker.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Guid.NewGuid(),
                    async (operationId, token) =>
                    {
                        _ = started[worker].TrySetResult(true);
                        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
                        return new ServerOperationResult(true, null, null);
                    },
                    null,
                    null,
                    CancellationToken.None).ConfigureAwait(true);
            }

            _ = await Task.WhenAll(started.Select(source => source.Task)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            TaskCompletionSource<bool> cleaned = new(TaskCreationOptions.RunContinuationsAsynchronously);
            OperationAcceptedDto pending = await executor.EnqueueAsync(
                OperationKind.ImportServerArchive,
                "synthetic:pending",
                Guid.NewGuid(),
                (_, _) => Task.FromResult(new ServerOperationResult(true, null, null)),
                null,
                cleanupToken =>
                {
                    _ = cleaned.TrySetResult(true);
                    return Task.CompletedTask;
                },
                CancellationToken.None).ConfigureAwait(true);

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(10));
            await executor.StopAsync(stopTimeout.Token).ConfigureAwait(true);
            _ = await cleaned.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
            OperationDetailsDto pendingDetails = (await store.FindOperationAsync(pending.OperationId, CancellationToken.None).ConfigureAwait(true))!;
            Assert.Equal("Failed", pendingDetails.Operation.Status);
            Assert.Equal("operation-interrupted", pendingDetails.Operation.ErrorCode);
        }
        finally
        {
            await factory.DisposeAsync().ConfigureAwait(true);
            await baseFactory.DisposeAsync().ConfigureAwait(true);
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

    private static async Task<OperationDetailsDto> WaitForTerminalAsync(ControlPlaneStore store, Guid operationId)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (true)
        {
            OperationDetailsDto? details = await store.FindOperationAsync(operationId, timeout.Token).ConfigureAwait(true);
            if (details?.Operation.Status is "Succeeded" or "Failed" or "Cancelled")
            {
                return details;
            }

            await Task.Delay(10, timeout.Token).ConfigureAwait(true);
        }
    }
}
