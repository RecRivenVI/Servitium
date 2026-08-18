using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Authorization;
using Servitium.Application.Common;
using Servitium.Application.Distribution;
using Servitium.Application.Instances;
using Servitium.Contracts.Distribution;
using Servitium.Contracts.Instances;
using Servitium.Domain.Distribution;
using Servitium.Domain.Files;
using Servitium.Domain.Identity;
using Servitium.Domain.Operations;

using Xunit;

namespace Servitium.Application.Tests;

public sealed class CoreUseCaseTests
{
    private static readonly Guid NodeId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid UploadId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid ChannelId = Guid.Parse("30000000-0000-4000-8000-000000000001");

    [Fact]
    [Trait("Category", "Unit")]
    public async Task 外部目录用例_执行授权预校验并建立长操作()
    {
        var operations = new FakeOperationTracker();
        var validator = new FakePathValidator(PathValidationResult.Success("D:\\Synthetic\\Server"));
        var useCase = new RegisterExternalDirectoryUseCase(new AuthorizationService(), validator, operations);
        AttachInstanceDirectoryRequest request = AttachRequest();

        ApplicationResult<Guid> success = await useCase.ExecuteAsync(Admin(), request, CancellationToken.None).ConfigureAwait(true);
        Assert.True(success.Succeeded);
        Assert.Equal(OperationKind.CreateInstance, operations.LastKind);

        validator.Result = PathValidationResult.Failure("instance-path-invalid");
        ApplicationResult<Guid> invalid = await useCase.ExecuteAsync(Admin(), request, CancellationToken.None).ConfigureAwait(true);
        Assert.False(invalid.Succeeded);
        Assert.Equal("instance-path-invalid", invalid.ErrorCode);

        ApplicationResult<Guid> denied = await useCase.ExecuteAsync(User(), request, CancellationToken.None).ConfigureAwait(true);
        Assert.False(denied.Succeeded);
        Assert.Equal("forbidden", denied.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task 归档与Prism用例_仅管理员可建立操作和预览()
    {
        var operations = new FakeOperationTracker();
        var archive = new ImportServerArchiveUseCase(new AuthorizationService(), operations);
        var archiveRequest = new ImportServerArchiveRequest(UploadId, "Server", NodeId, LaunchRequest());
        Assert.True((await archive.ExecuteAsync(Admin(), archiveRequest, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal(OperationKind.ImportServerArchive, operations.LastKind);
        Assert.False((await archive.ExecuteAsync(User(), archiveRequest, CancellationToken.None).ConfigureAwait(true)).Succeeded);

        var inspector = new FakeInspector();
        var preview = new PreviewPrismInstanceUseCase(new AuthorizationService(), inspector);
        ApplicationResult<PrismInstancePreviewDto> previewResult = await preview
            .ExecuteAsync(Admin(), new PreviewPrismInstanceRequest(UploadId), CancellationToken.None)
            .ConfigureAwait(true);
        Assert.True(previewResult.Succeeded);
        Assert.Equal("Synthetic Prism", previewResult.Value!.InstanceName);
        Assert.False((await preview.ExecuteAsync(User(), new PreviewPrismInstanceRequest(UploadId), CancellationToken.None).ConfigureAwait(true)).Succeeded);

        var convert = new ConvertPrismInstanceUseCase(new AuthorizationService(), operations);
        var convertRequest = new ConvertPrismInstanceRequest(UploadId, ChannelId, "Release", "1.0.0");
        Assert.True((await convert.ExecuteAsync(Admin(), convertRequest, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal(OperationKind.ConvertPrismInstance, operations.LastKind);
        Assert.False((await convert.ExecuteAsync(User(), convertRequest, CancellationToken.None).ConfigureAwait(true)).Succeeded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Channel用例_拒绝重复与未授权并保存合法Channel()
    {
        var repository = new FakeChannelRepository();
        var unitOfWork = new FakeUnitOfWork();
        var useCase = new CreateHmclChannelUseCase(
            new AuthorizationService(),
            repository,
            new FixedGuidFactory(ChannelId),
            new FixedClock(),
            unitOfWork);

        ApplicationResult<Guid> created = await useCase.ExecuteAsync(
            Admin(),
            new CreateHmclChannelRequest("stable", "Stable", "public"),
            CancellationToken.None).ConfigureAwait(true);
        Assert.True(created.Succeeded);
        Assert.Equal(ChannelVisibility.Public, repository.Channel!.Visibility);
        Assert.Equal(1, unitOfWork.SaveCount);

        ApplicationResult<Guid> duplicate = await useCase.ExecuteAsync(
            Admin(),
            new CreateHmclChannelRequest("stable", "Stable", "token-protected"),
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("hmcl-channel-slug-taken", duplicate.ErrorCode);

        ApplicationResult<Guid> denied = await useCase.ExecuteAsync(
            User(),
            new CreateHmclChannelRequest("other", "Other", "token-protected"),
            CancellationToken.None).ConfigureAwait(true);
        Assert.Equal("forbidden", denied.ErrorCode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Release用例_按Channel授权建立长操作()
    {
        var operations = new FakeOperationTracker();
        var useCase = new CreateHmclReleaseUseCase(new AuthorizationService(), operations);

        Assert.True((await useCase.ExecuteAsync(Admin(), ChannelId, CancellationToken.None).ConfigureAwait(true)).Succeeded);
        Assert.Equal(OperationKind.CreateHmclRelease, operations.LastKind);
        Assert.False((await useCase.ExecuteAsync(User(), ChannelId, CancellationToken.None).ConfigureAwait(true)).Succeeded);
    }

    private static AuthorizationPrincipal Admin()
    {
        return new(Guid.NewGuid(), Role.Administrator, new HashSet<Guid>());
    }

    private static AuthorizationPrincipal User()
    {
        return new(Guid.NewGuid(), Role.User, new HashSet<Guid>());
    }

    private static StructuredLaunchRequest LaunchRequest()
    {
        return new(Guid.NewGuid(), 512, 1024, [], "jar", "server.jar", ["nogui"]);
    }

    private static AttachInstanceDirectoryRequest AttachRequest()
    {
        return new("Synthetic", NodeId, "D:\\Synthetic\\Server", LaunchRequest());
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FixedGuidFactory(Guid value) : IGuidFactory
    {
        public Guid Create()
        {
            return value;
        }
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public int SaveCount { get; private set; }

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChannelRepository : IHmclChannelRepository
    {
        public HmclChannel? Channel { get; private set; }

        public Task AddAsync(HmclChannel channel, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Channel = channel;
            return Task.CompletedTask;
        }

        public Task<HmclChannel?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Channel?.Id == id ? Channel : null);
        }

        public Task<HmclChannel?> FindBySlugAsync(string slug, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Channel?.Slug == slug ? Channel : null);
        }
    }

    private sealed class FakePathValidator(PathValidationResult result) : IPathValidator
    {
        public PathValidationResult Result { get; set; } = result;

        public Task<PathValidationResult> ValidateWorkingDirectoryAsync(Guid nodeId, string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeOperationTracker : IOperationTracker
    {
        public OperationKind? LastKind { get; private set; }

        public Task<Operation> CreateAsync(OperationKind kind, string target, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastKind = kind;
            return Task.FromResult(new Operation(Guid.NewGuid(), kind, target, new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero)));
        }

        public Task ReportProgressAsync(Guid operationId, double progress, string stage, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeInspector : IInstanceImportInspector
    {
        public Task<PrismInstancePreviewDto> PreviewPrismAsync(Guid uploadId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new PrismInstancePreviewDto(
                "Synthetic Prism",
                "1.21.1",
                "Fabric",
                ["mmc-pack.json", ".minecraft/mods/example.jar"],
                new Dictionary<string, string> { ["instance.cfg"] = "metadata" }));
        }
    }
}
