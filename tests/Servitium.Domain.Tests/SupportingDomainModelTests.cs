using System;
using System.Collections.Generic;

using Servitium.Domain.Auditing;
using Servitium.Domain.Common;
using Servitium.Domain.Distribution;
using Servitium.Domain.Files;
using Servitium.Domain.Identity;
using Servitium.Domain.Instances;
using Servitium.Domain.Java;
using Servitium.Domain.Nodes;
using Servitium.Domain.Scheduling;
using Servitium.Domain.Settings;

using Xunit;

namespace Servitium.Domain.Tests;

public sealed class SupportingDomainModelTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 8, 3, 8, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    [Trait("Category", "Unit")]
    public void 标识时间与基础值对象_保持Guid和UTC语义()
    {
        var id = Guid.NewGuid();
        var first = new Node(id, "Node A", Epoch);
        var equal = new Node(id, "Node B", Epoch);
        var different = new Node(Guid.NewGuid(), "Node A", Epoch);

        Assert.Equal(first, equal);
        Assert.True(first.Equals((object)equal));
        Assert.NotEqual(first, different);
        Assert.Equal(first.GetHashCode(), equal.GetHashCode());
        _ = Assert.Throws<ArgumentException>(() => new Node(Guid.Empty, "Node", Epoch));

        var domainEvent = new SyntheticDomainEvent(Guid.NewGuid(), Epoch);
        Assert.Equal(TimeSpan.Zero, domainEvent.OccurredAt.Offset);
        Assert.Equal(TimeSpan.Zero, Guard.AsUtc(Epoch).Offset);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void 身份节点与Java模型_覆盖可变边界和删除规则()
    {
        var userId = Guid.NewGuid();
        var user = new User(userId, "tester", "Tester", Role.User, Epoch);
        user.Rename("Renamed");
        user.MarkSignedIn(Epoch);
        user.ChangeRole(Role.Administrator);
        Assert.Equal("Renamed", user.DisplayName);
        Assert.Equal(TimeSpan.Zero, user.LastSignedInAt!.Value.Offset);
        user.Disable();
        user.Delete();
        Assert.True(user.IsDeleted);

        var session = new Session(Guid.NewGuid(), userId, "hash", Epoch.AddHours(1), Epoch);
        var credential = new UserCredential(Guid.NewGuid(), userId, "argon2id", Epoch);
        var apiKey = new ApiKey(Guid.NewGuid(), userId, "automation", "hash", Epoch, null);
        var external = new ExternalIdentity(Guid.NewGuid(), userId, "https://issuer.example", "subject");
        var windows = new WindowsIdentityBinding(Guid.NewGuid(), userId, "S-1-5-21-1");
        Assert.Equal(userId, session.UserId);
        Assert.Equal("argon2id", credential.PasswordHash);
        Assert.Null(apiKey.ExpiresAt);
        Assert.Equal("subject", external.Subject);
        Assert.StartsWith("S-1-5", windows.Sid, StringComparison.Ordinal);

        var nodeId = Guid.NewGuid();
        var node = new Node(nodeId, "Node", Epoch);
        node.Rename("Renamed node");
        node.SetStatus(NodeStatus.Online);
        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal(TimeSpan.Zero, node.CreatedAt.Offset);
        var nodeCredential = new NodeCredential(Guid.NewGuid(), nodeId, "hash", Epoch, null);
        var token = new NodeRegistrationToken(Guid.NewGuid(), "token-hash", Epoch.AddMinutes(5), null);
        Assert.Equal(nodeId, nodeCredential.NodeId);
        Assert.Null(token.ConsumedAt);

        var imported = new JavaRuntime(Guid.NewGuid(), nodeId, "D:\\Java\\bin\\java.exe", "25", "OpenJDK", JavaArchitecture.Arm64, JavaRuntimeSource.Imported);
        imported.EnsureFilesCanBeDeleted();
        Assert.True(imported.CanDeleteFiles);
        Assert.Equal(JavaArchitecture.Arm64, imported.Architecture);
        var discovered = new JavaRuntime(Guid.NewGuid(), nodeId, "C:\\Java\\bin\\java.exe", "21", "OpenJDK", JavaArchitecture.X64, JavaRuntimeSource.Discovered);
        Assert.False(discovered.CanDeleteFiles);
        Assert.Equal("java-runtime-not-imported", Assert.Throws<DomainException>(discovered.EnsureFilesCanBeDeleted).Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void 审计文件设置和实例辅助类型_构造后只暴露受控值()
    {
        var id = Guid.NewGuid();
        var activity = new ActivityLogEntry(
            id,
            null,
            "system",
            "instance.start",
            "instance:1",
            Epoch,
            new Dictionary<string, string> { ["source"] = "test" });
        var audit = new AuditLogEntry(
            Guid.NewGuid(),
            id,
            "tester",
            AuditCategory.DestructiveAction,
            "instance.unregister",
            "instance:1",
            Epoch,
            "succeeded");
        Assert.Equal("instance.start", activity.Action);
        Assert.Equal(AuditCategory.DestructiveAction, audit.Category);
        Assert.Equal(TimeSpan.Zero, audit.OccurredAt.Offset);

        var filePath = new FilePath("mods/example.jar");
        var valid = PathValidationResult.Success("D:\\Synthetic");
        var invalid = PathValidationResult.Failure("instance-path-invalid");
        Assert.Equal("mods/example.jar", filePath.Value);
        Assert.True(valid.IsValid);
        Assert.False(invalid.IsValid);
        _ = Assert.Throws<ArgumentException>(() => new FilePath("bad\npath"));

        var settings = new SystemSettings("zh-Hans", 8, 64, 8);
        Assert.Equal(64, settings.MaximumInstances);
        _ = Assert.Throws<ArgumentException>(() => new SystemSettings("fr-FR", 8, 64, 8));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new SystemSettings("en-US", 9, 64, 8));

        var autoStart = new AutoStartPolicy(true, true, 3);
        var terminal = new TerminalSettings("UTF-8", true, true);
        var rcon = new RconSettings(true, 25575, "secret-ref");
        var query = new QuerySettings(true, 25565);
        Assert.True(autoStart.RestartAfterCrash);
        Assert.Equal("UTF-8", terminal.EncodingName);
        Assert.Equal(25575, rcon.Port);
        Assert.True(query.Enabled);

        var unc = new WorkingDirectory("\\\\server\\share\\minecraft", false);
        Assert.StartsWith("\\\\server", unc.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void 分发与计划辅助行为_只经显式方法变化()
    {
        var channel = new HmclChannel(Guid.NewGuid(), "stable", "Stable", ChannelVisibility.TokenProtected, Epoch);
        var releaseId = Guid.NewGuid();
        channel.Publish(releaseId);
        Assert.Equal(releaseId, channel.ActiveReleaseId);

        var instanceId = Guid.NewGuid();
        var task = new ScheduledTask(
            Guid.NewGuid(),
            instanceId,
            "Task",
            new SpecifiedTrigger(Epoch.AddMinutes(1)),
            [new ScheduleAction(ScheduleActionKind.SendConsoleCommand, "say synthetic", true)],
            -1,
            OverlapPolicy.Queue,
            true);
        task.Rename("Renamed task");
        task.MarkScheduledRunTriggered();
        Assert.Equal(-1, task.RemainingRuns);
        Assert.Equal("Renamed task", task.Name);
        _ = Assert.Throws<ArgumentException>(() => new CalendarTrigger([], new TimeOnly(8, 0)));

        var run = new TaskRun(Guid.NewGuid(), task.Id, Epoch, Epoch.AddSeconds(1), true, null);
        Assert.True(run.Succeeded);
        Assert.Null(run.FailureCode);
    }

    private sealed record SyntheticDomainEvent(Guid Id, DateTimeOffset At) : DomainEvent(Id, At);
}
