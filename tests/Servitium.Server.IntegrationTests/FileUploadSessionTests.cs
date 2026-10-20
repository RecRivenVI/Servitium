using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Application.Abstractions;
using Servitium.Application.Files;
using Servitium.Contracts.Files;
using Servitium.Domain.Primitives;
using Servitium.Server.Files;
using Servitium.Server.Hosting;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class FileUploadSessionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-820")]
    [Trait("TestId", "INT-830")]
    public async Task 分片上传_乱序重复断点续传与合并哈希一致()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-upload-session-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            FakeUploadStore store = new();
            MutableClock clock = new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
            FileUploadSessionManager manager = new(store, new ServerHostOptions { DataRoot = root }, clock);
            byte[] content = [.. Enumerable.Range(0, 700_000).Select(index => (byte)(index * 31 % 251))];
            var instanceId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            UploadSessionDto created = await manager.CreateAsync(
                instanceId,
                nodeId,
                new("synthetic.bin", "uploads/synthetic.bin", content.LongLength, 256 * 1024),
                CancellationToken.None).ConfigureAwait(true);

            foreach (int index in new[] { 2, 0, 1 })
            {
                byte[] part = [.. content.Skip(index * created.PartSizeBytes).Take(created.PartSizeBytes)];
                using MemoryStream stream = new(part, writable: false);
                UploadSessionDto updated = await manager.SavePartAsync(
                    created.Id,
                    instanceId,
                    nodeId,
                    index,
                    Convert.ToHexString(SHA256.HashData(part)),
                    stream,
                    CancellationToken.None).ConfigureAwait(true);
                Assert.Contains(index, updated.ReceivedParts);
            }

            byte[] duplicate = [.. content.Take(created.PartSizeBytes)];
            using (MemoryStream stream = new(duplicate, writable: false))
            {
                UploadSessionDto resumed = await manager.SavePartAsync(
                    created.Id,
                    instanceId,
                    nodeId,
                    0,
                    Convert.ToHexString(SHA256.HashData(duplicate)),
                    stream,
                    CancellationToken.None).ConfigureAwait(true);
                Assert.Equal([0, 1, 2], resumed.ReceivedParts);
            }

            (StoredUploadSession session, string path) = await manager.AssembleAsync(
                created.Id,
                instanceId,
                nodeId,
                Convert.ToHexString(SHA256.HashData(content)),
                CancellationToken.None).ConfigureAwait(true);
            Assert.Equal(content, await File.ReadAllBytesAsync(path).ConfigureAwait(true));
            Assert.Equal("uploads/synthetic.bin", session.DestinationPath);

            await manager.CancelAsync(created.Id, instanceId, nodeId, CancellationToken.None).ConfigureAwait(true);
            Assert.Null(await store.FindAsync(created.Id, CancellationToken.None).ConfigureAwait(true));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-150")]
    public async Task 分片上传_拒绝路径穿越错误校验和与跨实例访问()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-upload-session-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            FakeUploadStore store = new();
            MutableClock clock = new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
            FileUploadSessionManager manager = new(store, new ServerHostOptions { DataRoot = root }, clock);
            _ = await Assert.ThrowsAsync<DomainException>(() => manager.CreateAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new("unsafe.bin", "../escape.bin", 1, 256 * 1024),
                CancellationToken.None)).ConfigureAwait(true);

            var instanceId = Guid.NewGuid();
            var nodeId = Guid.NewGuid();
            UploadSessionDto session = await manager.CreateAsync(
                instanceId,
                nodeId,
                new("safe.bin", "safe.bin", 1, 256 * 1024),
                CancellationToken.None).ConfigureAwait(true);
            using MemoryStream byteStream = new([42], writable: false);
            DomainException checksum = await Assert.ThrowsAsync<DomainException>(() => manager.SavePartAsync(
                session.Id,
                instanceId,
                nodeId,
                0,
                new string('0', 64),
                byteStream,
                CancellationToken.None)).ConfigureAwait(true);
            Assert.Equal("checksum-mismatch", checksum.Code);

            _ = await Assert.ThrowsAsync<DomainException>(() => manager.GetAsync(
                session.Id,
                Guid.NewGuid(),
                nodeId,
                CancellationToken.None)).ConfigureAwait(true);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-820")]
    public async Task 分片上传_过期清理删除记录和暂存目录()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "servitium-upload-session-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            FakeUploadStore store = new();
            MutableClock clock = new(new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero));
            FileUploadSessionManager manager = new(store, new ServerHostOptions { DataRoot = root }, clock);
            UploadSessionDto session = await manager.CreateAsync(
                Guid.NewGuid(),
                Guid.NewGuid(),
                new("expired.bin", "expired.bin", 0, 256 * 1024),
                CancellationToken.None).ConfigureAwait(true);
            clock.Advance(TimeSpan.FromHours(25));

            await manager.CleanupExpiredAsync(CancellationToken.None).ConfigureAwait(true);

            Assert.Null(await store.FindAsync(session.Id, CancellationToken.None).ConfigureAwait(true));
            Assert.False(Directory.Exists(Path.Combine(root, "staging", "file-uploads", session.Id.ToString("N"))));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeUploadStore : IUploadSessionStore
    {
        private readonly ConcurrentDictionary<Guid, StoredUploadSession> _sessions = new();

        public Task AddAsync(StoredUploadSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.True(_sessions.TryAdd(session.Id, session));
            return Task.CompletedTask;
        }

        public Task<StoredUploadSession?> FindAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _sessions.TryGetValue(id, out StoredUploadSession? session);
            return Task.FromResult(session);
        }

        public Task<StoredUploadSession> MarkPartReceivedAsync(Guid id, int index, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StoredUploadSession updated = _sessions.AddOrUpdate(
                id,
                _ => throw new DomainException("upload-session-expired", "The upload session does not exist."),
                (_, current) => current with { ReceivedParts = [.. current.ReceivedParts.Append(index).Distinct().Order()] });
            return Task.FromResult(updated);
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = _sessions.TryRemove(id, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StoredUploadSession>> ListExpiredAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<StoredUploadSession> result = [.. _sessions.Values.Where(session => session.ExpiresAt <= now)];
            return Task.FromResult(result);
        }
    }

    private sealed class MutableClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = value;

        internal void Advance(TimeSpan valueToAdd)
        {
            UtcNow = UtcNow.Add(valueToAdd);
        }
    }
}
