using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

using Servitium.Agent.Archives;
using Servitium.Agent.ControlPlane;
using Servitium.Agent.Files;
using Servitium.Agent.Instances;
using Servitium.Agent.Paths;
using Servitium.Agent.Storage;
using Servitium.Application.Abstractions;
using Servitium.Contracts.Agent;
using Servitium.Contracts.Agent.V1;
using Servitium.Contracts.Instances;
using Servitium.Domain.Primitives;
using Servitium.Infrastructure.Networking;
using Servitium.Infrastructure.Persistence;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentUploadSessionServiceTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-520")]
    [Trait("TestId", "INT-820")]
    [Trait("TestId", "INT-830")]
    public async Task 分块上传_乱序重试重启后由Agent校验合并()
    {
        UploadContext test = await UploadContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        const int partSize = 256 * 1024;
        byte[] payload = [.. Enumerable.Range(0, (partSize * 2) + 37).Select(index => (byte)(index * 31 % 251))];
        var uploadId = Guid.NewGuid();
        DateTimeOffset expiresAt = test.Clock.UtcNow.AddHours(1);

        foreach (int index in new[] { 2, 0, 1 })
        {
            byte[] part = [.. payload.Skip(index * partSize).Take(partSize)];
            await test.StorePartAsync(uploadId, test.InstanceId, index, 3, partSize, payload.Length, part, expiresAt).ConfigureAwait(true);
        }

        byte[] first = [.. payload.Take(partSize)];
        using (AgentDbContext beforeRetry = test.Factory.CreateDbContext())
        {
            LocalUploadPartRow stored = await beforeRetry.UploadParts.SingleAsync(row => row.UploadId == uploadId && row.PartIndex == 0).ConfigureAwait(true);
            Assert.Equal(test.InstanceId, stored.InstanceId);
            Assert.Equal(partSize, stored.Size);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(first)), stored.Sha256);
        }

        await test.StorePartAsync(uploadId, test.InstanceId, 0, 3, partSize, payload.Length, first, expiresAt).ConfigureAwait(true);
        using (AgentDbContext context = test.Factory.CreateDbContext())
        {
            Assert.Equal(3, await context.UploadParts.CountAsync().ConfigureAwait(true));
        }

        await test.ReopenTransferStoreAsync().ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(test.WorkingDirectory, "nested"));
        string fullHash = Convert.ToHexString(SHA256.HashData(payload));
        _ = await test.Service.CompleteAsync(
            new(test.InstanceId, uploadId, "nested/payload.bin", 3, payload.Length, fullHash, false),
            CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(payload, await File.ReadAllBytesAsync(Path.Combine(test.WorkingDirectory, "nested", "payload.bin")).ConfigureAwait(true));
        using (AgentDbContext context = test.Factory.CreateDbContext())
        {
            Assert.Empty(await context.UploadParts.ToArrayAsync().ConfigureAwait(true));
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "SEC-520")]
    [Trait("TestId", "SEC-180")]
    public async Task 分块上传_拒绝错误校验和与跨实例冲突并清理过期数据()
    {
        UploadContext test = await UploadContext.CreateAsync().ConfigureAwait(true);
        await using ConfiguredAsyncDisposable cleanup = test.ConfigureAwait(true);
        const int partSize = 256 * 1024;
        byte[] payload = [.. Enumerable.Repeat((byte)0x5a, partSize)];
        var uploadId = Guid.NewGuid();

        DomainException checksum = await Assert.ThrowsAsync<DomainException>(() => test.StorePartAsync(
            uploadId,
            test.InstanceId,
            0,
            1,
            partSize,
            payload.Length,
            payload,
            test.Clock.UtcNow.AddHours(1),
            new string('0', 64))).ConfigureAwait(true);
        Assert.Equal("checksum-mismatch", checksum.Code);

        await test.StorePartAsync(uploadId, test.InstanceId, 0, 1, partSize, payload.Length, payload, test.Clock.UtcNow.AddMinutes(-1)).ConfigureAwait(true);
        DomainException conflict = await Assert.ThrowsAsync<DomainException>(() => test.StorePartAsync(
            uploadId,
            Guid.NewGuid(),
            0,
            1,
            partSize,
            payload.Length,
            payload,
            test.Clock.UtcNow.AddHours(1))).ConfigureAwait(true);
        Assert.Equal("conflict", conflict.Code);

        await test.Service.CleanupExpiredAsync(CancellationToken.None).ConfigureAwait(true);
        using AgentDbContext context = test.Factory.CreateDbContext();
        Assert.Empty(await context.UploadParts.ToArrayAsync().ConfigureAwait(true));
        Assert.False(Directory.Exists(Path.Combine(test.Storage.StagingRoot, "file-uploads", uploadId.ToString("N"))));
    }

    private sealed class UploadContext : IAsyncDisposable
    {
        private readonly string _root;
        private readonly SqliteWriteCoordinator _coordinator;
        private readonly InstanceFileService _files;
        private InboundFileTransferStore _transfers;

        private UploadContext(
            string root,
            TestContextFactory factory,
            AgentStorageOptions storage,
            SqliteWriteCoordinator coordinator,
            InstanceFileService files,
            InboundFileTransferStore transfers,
            AgentUploadSessionService service,
            MutableClock clock,
            Guid instanceId,
            string workingDirectory)
        {
            _root = root;
            Factory = factory;
            Storage = storage;
            _coordinator = coordinator;
            _files = files;
            _transfers = transfers;
            Service = service;
            Clock = clock;
            InstanceId = instanceId;
            WorkingDirectory = workingDirectory;
        }

        internal TestContextFactory Factory { get; }
        internal AgentStorageOptions Storage { get; }
        internal AgentUploadSessionService Service { get; private set; }
        internal MutableClock Clock { get; }
        internal Guid InstanceId { get; }
        internal string WorkingDirectory { get; }

        internal static async Task<UploadContext> CreateAsync()
        {
            string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-agent-upload-" + Guid.NewGuid().ToString("N"))).FullName;
            AgentStorageOptions storage = new(root, Path.Combine(root, "agent.db"), Path.Combine(root, "instances"), Path.Combine(root, "staging"));
            _ = Directory.CreateDirectory(storage.ManagedInstanceRoot);
            _ = Directory.CreateDirectory(storage.StagingRoot);
            DbContextOptions<AgentDbContext> options = new DbContextOptionsBuilder<AgentDbContext>()
                .UseSqlite("Data Source=" + storage.DatabasePath + ";Pooling=False")
                .Options;
            TestContextFactory factory = new(options);
            using (AgentDbContext context = factory.CreateDbContext())
            {
                await context.Database.MigrateAsync().ConfigureAwait(true);
            }

            SqliteWriteCoordinator coordinator = new();
            WindowsPathPolicy pathPolicy = new();
            SafeZipExtractor zipExtractor = new(ArchiveSafetyLimits.Default);
            InstanceRegistry instances = new(factory, coordinator, pathPolicy, zipExtractor, storage, new TestSecretProtector());
            var instanceId = Guid.NewGuid();
            LocalInstanceSnapshot instance = await instances.CreateEmptyAsync(
                instanceId,
                "Synthetic Upload",
                new StructuredLaunchRequest(Guid.NewGuid(), 64, 128, [], "jar", "server.jar", []),
                [],
                null,
                DateTimeOffset.UtcNow,
                CancellationToken.None).ConfigureAwait(true);
            RestrictedOutboundHttpClient outbound = new(
                new SystemHostAddressResolver(),
                new RestrictedHttpOptions(new HashSet<string>(StringComparer.OrdinalIgnoreCase), 1024 * 1024, TimeSpan.FromSeconds(5), 0));
            InstanceFileService files = new(instances, pathPolicy, zipExtractor, outbound);
            InboundFileTransferStore transfers = new(storage);
            MutableClock clock = new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
            AgentUploadSessionService service = new(factory, coordinator, storage, transfers, files, clock);
            return new(root, factory, storage, coordinator, files, transfers, service, clock, instanceId, instance.WorkingDirectory);
        }

        internal async Task StorePartAsync(
            Guid uploadId,
            Guid instanceId,
            int index,
            int partCount,
            long partSize,
            long totalBytes,
            byte[] content,
            DateTimeOffset expiresAt,
            string? declaredHash = null)
        {
            string streamId = Guid.NewGuid().ToString("N");
            await _transfers.HandleControlAsync(new StreamControl
            {
                StreamId = streamId,
                Kind = StreamKind.FileUpload,
                Action = StreamControlAction.Open,
                ResourceId = System.Text.Json.JsonSerializer.Serialize(new AgentInboundTransferDescriptor("part", content.LongLength)),
            }, CancellationToken.None).ConfigureAwait(true);
            ulong sequence = 0;
            for (int offset = 0; offset < content.Length; offset += 256 * 1024)
            {
                int count = Math.Min(256 * 1024, content.Length - offset);
                await _transfers.HandleChunkAsync(new StreamChunk
                {
                    StreamId = streamId,
                    Sequence = sequence++,
                    Data = ByteString.CopyFrom(content, offset, count),
                }, CancellationToken.None).ConfigureAwait(true);
            }

            await _transfers.HandleControlAsync(new StreamControl
            {
                StreamId = streamId,
                Kind = StreamKind.FileUpload,
                Action = StreamControlAction.Close,
            }, CancellationToken.None).ConfigureAwait(true);
            string hash = declaredHash ?? Convert.ToHexString(SHA256.HashData(content));
            await Service.StorePartAsync(
                new(instanceId, uploadId, index, partCount, partSize, totalBytes, hash, expiresAt, streamId),
                CancellationToken.None).ConfigureAwait(true);
        }

        internal async Task ReopenTransferStoreAsync()
        {
            await _transfers.DisposeAsync().ConfigureAwait(true);
            Service.Dispose();
            _transfers = new InboundFileTransferStore(Storage);
            Service = new AgentUploadSessionService(Factory, _coordinator, Storage, _transfers, _files, Clock);
        }

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await _transfers.DisposeAsync().ConfigureAwait(true);
            _coordinator.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            GC.SuppressFinalize(this);
        }
    }

    internal sealed class TestContextFactory(DbContextOptions<AgentDbContext> options) : IDbContextFactory<AgentDbContext>
    {
        public AgentDbContext CreateDbContext()
        {
            return new(options);
        }
    }

    internal sealed class MutableClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            return plaintext.ToArray();
        }

        public byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        {
            return ciphertext.ToArray();
        }
    }
}
