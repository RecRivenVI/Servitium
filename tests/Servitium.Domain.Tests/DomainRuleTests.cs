using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Domain.Primitives;
using Servitium.Domain.Distribution;
using Servitium.Domain.Identity;
using Servitium.Domain.Instances;
using Servitium.Domain.Java;
using Servitium.Domain.Operations;
using Servitium.Domain.Scheduling;

using Xunit;

namespace Servitium.Domain.Tests;

public sealed class DomainRuleTests
{
    private static readonly Guid NodeId = Guid.Parse("10000000-0000-4000-8000-000000000001");
    private static readonly Guid RuntimeId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly DateTimeOffset Epoch = new(2026, 8, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-100")]
    public void 实例状态机_合法转换均到达规定状态()
    {
        Instance instance = CreateInstance();

        instance.Start();
        Assert.Equal(InstanceStatus.Starting, instance.Status);
        instance.MarkStarted();
        Assert.Equal(InstanceStatus.Running, instance.Status);
        instance.Stop();
        Assert.Equal(InstanceStatus.Stopping, instance.Status);
        instance.MarkStopped();
        Assert.Equal(InstanceStatus.Stopped, instance.Status);

        instance.Start();
        instance.MarkStartFailed(processCrashed: false);
        Assert.Equal(InstanceStatus.Stopped, instance.Status);

        instance.Start();
        instance.MarkStartFailed(processCrashed: true);
        Assert.Equal(InstanceStatus.Crashed, instance.Status);
        instance.BeginAutomaticRestart();
        Assert.Equal(InstanceStatus.Starting, instance.Status);
        instance.MarkStarted();
        instance.MarkCrashed();
        Assert.Equal(InstanceStatus.Crashed, instance.Status);

        instance.BeginBusyOperation();
        Assert.Equal(InstanceStatus.Busy, instance.Status);
        instance.CompleteBusyOperation();
        Assert.Equal(InstanceStatus.Crashed, instance.Status);

        instance.MarkUnreachable();
        Assert.Equal(InstanceStatus.Unreachable, instance.Status);
        instance.RestoreReachable(InstanceStatus.Running);
        Assert.Equal(InstanceStatus.Running, instance.Status);
        instance.ForceStop();
        Assert.Equal(InstanceStatus.Stopped, instance.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-100")]
    public void 实例状态机_非法转换携带稳定错误码()
    {
        Instance stopped = CreateInstance();
        InvalidInstanceStateTransitionException startOnly = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.Stop);
        Assert.Equal("conflict", startOnly.Code);

        stopped.Start();
        InvalidInstanceStateTransitionException notStopped = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.Start);
        Assert.Equal("instance-not-stopped", notStopped.Code);
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.MarkStopped);
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.MarkCrashed);
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.CompleteBusyOperation);

        stopped.MarkUnreachable();
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.MarkUnreachable);
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(() => stopped.RestoreReachable(InstanceStatus.Unreachable));
        _ = Assert.Throws<InvalidInstanceStateTransitionException>(stopped.BeginBusyOperation);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-110")]
    public void Owner_删除降级禁用均被拒绝且目录恒有唯一Owner()
    {
        User owner = CreateUser(Role.Owner, "owner");
        Assert.Equal("owner-immutable", Assert.Throws<DomainException>(owner.Delete).Code);
        Assert.Equal("owner-immutable", Assert.Throws<DomainException>(owner.Disable).Code);
        Assert.Equal("owner-immutable", Assert.Throws<DomainException>(() => owner.ChangeRole(Role.Administrator)).Code);

        var directory = new UserDirectory(owner);
        User administrator = CreateUser(Role.Administrator, "admin");
        directory.Add(administrator);
        Assert.Equal("owner-immutable", Assert.Throws<DomainException>(() => directory.Add(CreateUser(Role.Owner, "other"))).Code);
        Assert.Equal("user-name-taken", Assert.Throws<DomainException>(() => directory.Add(CreateUser(Role.User, "ADMIN"))).Code);

        directory.TransferOwnership(owner.Id, administrator.Id);
        Assert.Equal(Role.Administrator, owner.Role);
        Assert.Equal(Role.Owner, administrator.Role);
        _ = Assert.Single(directory.Users, user => user.Role == Role.Owner);
    }

    public static TheoryData<int, int, string[], LaunchTargetKind, string, string[], string[]> StructuredLaunchCases
    {
        get
        {
            var data = new TheoryData<int, int, string[], LaunchTargetKind, string, string[], string[]>
            {
                { 256, 512, [], LaunchTargetKind.Jar, "server.jar", [], ["java.exe", "-Xms256m", "-Xmx512m", "-jar", "server.jar"] },
                { 512, 512, ["-Dfile.encoding=UTF-8"], LaunchTargetKind.Jar, "paper.jar", [], ["java.exe", "-Xms512m", "-Xmx512m", "-Dfile.encoding=UTF-8", "-jar", "paper.jar"] },
                { 1024, 2048, ["-XX:+UseG1GC"], LaunchTargetKind.Jar, "fabric.jar", ["nogui"], ["java.exe", "-Xms1024m", "-Xmx2048m", "-XX:+UseG1GC", "-jar", "fabric.jar", "nogui"] },
                { 2048, 4096, ["-XX:+UseZGC", "-Duser.language=en"], LaunchTargetKind.Jar, "forge.jar", ["--nogui"], ["java.exe", "-Xms2048m", "-Xmx4096m", "-XX:+UseZGC", "-Duser.language=en", "-jar", "forge.jar", "--nogui"] },
                { 128, 256, [], LaunchTargetKind.ArgumentFile, "args.txt", [], ["java.exe", "-Xms128m", "-Xmx256m", "@args.txt"] },
                { 768, 1536, ["-Xlog:gc"], LaunchTargetKind.ArgumentFile, "libraries/args.txt", ["nogui"], ["java.exe", "-Xms768m", "-Xmx1536m", "-Xlog:gc", "@libraries/args.txt", "nogui"] },
                { 4096, 8192, ["-XX:+AlwaysPreTouch"], LaunchTargetKind.Jar, "server file.jar", [], ["java.exe", "-Xms4096m", "-Xmx8192m", "-XX:+AlwaysPreTouch", "-jar", "server file.jar"] },
                { 64, 64, [], LaunchTargetKind.Jar, "a.jar", ["--port", "25565"], ["java.exe", "-Xms64m", "-Xmx64m", "-jar", "a.jar", "--port", "25565"] },
                { 300, 600, ["-Da=b"], LaunchTargetKind.ArgumentFile, "unix_args.txt", ["--safeMode"], ["java.exe", "-Xms300m", "-Xmx600m", "-Da=b", "@unix_args.txt", "--safeMode"] },
                { 6000, 12000, ["-XX:MaxGCPauseMillis=100", "-Dlog4j2.formatMsgNoLookups=true"], LaunchTargetKind.Jar, "purpur.jar", ["nogui", "--world", "world"], ["java.exe", "-Xms6000m", "-Xmx12000m", "-XX:MaxGCPauseMillis=100", "-Dlog4j2.formatMsgNoLookups=true", "-jar", "purpur.jar", "nogui", "--world", "world"] }
            };
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StructuredLaunchCases))]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-120")]
    public void 结构化启动_生成固定顺序参数数组(
        int minimum,
        int maximum,
        string[] jvm,
        LaunchTargetKind kind,
        string target,
        string[] server,
        string[] expected)
    {
        var configuration = LaunchConfiguration.CreateStructured(
            CreateRuntime(), minimum, maximum, jvm, kind, target, server);

        Assert.Equal(expected, configuration.Arguments);
        Assert.Equal(LaunchMode.Structured, configuration.Mode);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-130")]
    public void 原始启动_首项必须精确匹配已登记Java且保持参数边界()
    {
        JavaRuntime runtime = CreateRuntime();
        string[] command = [runtime.ExecutablePath, "-jar", "server file.jar", "nogui"];

        var configuration = LaunchConfiguration.CreateRaw(command, [runtime]);

        Assert.Equal(command, configuration.Arguments);
        Assert.Equal(LaunchMode.Raw, configuration.Mode);
        DomainException exception = Assert.Throws<DomainException>(() =>
            LaunchConfiguration.CreateRaw(["C:\\unregistered\\java.exe", "-jar", "server.jar"], [runtime]));
        Assert.Equal("raw-command-invalid", exception.Code);
        _ = Assert.Throws<DomainException>(() => LaunchConfiguration.CreateRaw([runtime.ExecutablePath], [runtime]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-140")]
    public void 外部工作目录_永不允许递归删除()
    {
        var external = new WorkingDirectory("D:\\Minecraft\\External", ownedByServitium: false);
        var managed = new WorkingDirectory("D:\\Servitium\\Instances\\id", ownedByServitium: true);

        Assert.False(external.CanDeleteRecursively);
        Assert.True(managed.CanDeleteRecursively);
        Assert.Equal("instance-path-invalid", Assert.Throws<DomainException>(() => new WorkingDirectory("relative\\path", false)).Code);

        Instance instance = CreateInstance();
        instance.ChangeWorkingDirectory(external);
        Assert.False(instance.WorkingDirectory.OwnedByServitium);
        instance.Start();
        Assert.Equal("instance-not-stopped", Assert.Throws<InvalidInstanceStateTransitionException>(() => instance.ChangeWorkingDirectory(managed)).Code);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-150")]
    public void 计划任务_动作间隔次数名称与数量边界均被强制()
    {
        var instanceId = Guid.NewGuid();
        var action = new ScheduleAction(ScheduleActionKind.Start, null, false);
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => new IntervalTrigger(TimeSpan.FromSeconds(9)));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateTask(instanceId, "empty", [], 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateTask(instanceId, "many", Enumerable.Repeat(action, 11), 1));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => CreateTask(instanceId, "zero", [action], 0));
        _ = Assert.Throws<ArgumentException>(() => CreateTask(instanceId, "bad\nname", [action], 1));

        var set = new ScheduledTaskSet(instanceId, maximumTasks: 2);
        set.Add(CreateTask(instanceId, "daily", [action], 1));
        Assert.Equal("schedule-name-taken", Assert.Throws<DomainException>(() => set.Add(CreateTask(instanceId, "DAILY", [action], 1))).Code);
        set.Add(CreateTask(instanceId, "second", [action], -1));
        Assert.Equal("schedule-limit-reached", Assert.Throws<DomainException>(() => set.Add(CreateTask(instanceId, "third", [action], 1))).Code);

        ScheduledTask finite = CreateTask(instanceId, "finite", [action], 1);
        finite.MarkScheduledRunTriggered();
        Assert.Equal(0, finite.RemainingRuns);
        Assert.False(finite.Enabled);
        _ = Assert.Throws<DomainException>(finite.MarkScheduledRunTriggered);
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-160")]
    public void 计划触发_间隔日历指定时间覆盖跨日跨周与夏令时()
    {
        var interval = new IntervalTrigger(TimeSpan.FromSeconds(10));
        Assert.Equal(Epoch.AddSeconds(10), interval.NextAfter(Epoch, TimeZoneInfo.Utc));

        var specified = new SpecifiedTrigger(Epoch.AddHours(1));
        Assert.Equal(Epoch.AddHours(1), specified.NextAfter(Epoch, TimeZoneInfo.Utc));
        Assert.Null(specified.NextAfter(Epoch.AddHours(2), TimeZoneInfo.Utc));

        var calendar = new CalendarTrigger([DayOfWeek.Monday], new TimeOnly(8, 30));
        DateTimeOffset friday = new(2026, 8, 7, 23, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 8, 10, 8, 30, 0, TimeSpan.Zero), calendar.NextAfter(friday, TimeZoneInfo.Utc));

        var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
        var dstGap = new CalendarTrigger([DayOfWeek.Sunday], new TimeOnly(2, 30));
        DateTimeOffset beforeGap = new(2026, 3, 8, 9, 0, 0, TimeSpan.Zero);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero), dstGap.NextAfter(beforeGap, pacific));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-170")]
    public void Release_元数据清单哈希路径不可变且输入严格校验()
    {
        HmclReleaseFile file = CreateReleaseFile("mods/a.jar", 'a');
        HmclRelease release = CreateRelease([file]);

        Assert.Equal(1, release.FileCount);
        Assert.Equal(10, release.TotalSize);
        _ = Assert.Throws<NotSupportedException>(() => ((IList<HmclReleaseFile>)release.Files).Add(CreateReleaseFile("mods/b.jar", 'b')));
        _ = Assert.Throws<DomainException>(() => new ContentHash(new string('A', 40)));
        _ = Assert.Throws<DomainException>(() => CreateReleaseFile("../escape.jar", 'a'));
        _ = Assert.Throws<DomainException>(() => CreateRelease([file, CreateReleaseFile("MODS/A.JAR", 'b')]));
        _ = Assert.Throws<DomainException>(() => CreateRelease([]));
        _ = Assert.Throws<DomainException>(() => new HmclRelease(
            Guid.NewGuid(), Guid.NewGuid(), 1, "r", "a", "1", "", "1.21.1",
            new LoaderDeclaration(null, "1.0", null), [file], Epoch, Guid.NewGuid()));
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("release-1")]
    [InlineData("abc")]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-180")]
    public void Channel_接受合法Slug(string slug)
    {
        var channel = new HmclChannel(Guid.NewGuid(), slug, "Stable", ChannelVisibility.Public, Epoch);

        Assert.Equal(slug, channel.Slug);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("UPPER")]
    [InlineData("has space")]
    [InlineData("contains_underscore")]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-180")]
    public void Channel_拒绝非法Slug(string slug)
    {
        _ = Assert.Throws<DomainException>(() => new HmclChannel(Guid.NewGuid(), slug, "Stable", ChannelVisibility.Public, Epoch));
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-210")]
    public void 长操作_仅允许规定状态转换且提交点后不能取消()
    {
        var operation = new Operation(Guid.NewGuid(), OperationKind.CreateInstance, "instance", Epoch);
        operation.Start();
        operation.Succeed(Epoch.AddSeconds(1));
        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        _ = Assert.Throws<DomainException>(() => operation.Cancel(Epoch.AddSeconds(2), commitPointReached: false));
        _ = Assert.Throws<DomainException>(operation.Start);

        var failed = new Operation(Guid.NewGuid(), OperationKind.ImportServerArchive, "archive", Epoch);
        failed.Start();
        failed.Fail("archive-unsafe", Epoch.AddSeconds(2));
        Assert.Equal("archive-unsafe", failed.ErrorCode);

        var cancelled = new Operation(Guid.NewGuid(), OperationKind.ConvertPrismInstance, "upload", Epoch);
        cancelled.Cancel(Epoch, commitPointReached: false);
        Assert.Equal(OperationStatus.Cancelled, cancelled.Status);

        var committed = new Operation(Guid.NewGuid(), OperationKind.CreateHmclRelease, "channel", Epoch);
        Assert.Equal("operation-not-cancellable", Assert.Throws<DomainException>(() => committed.Cancel(Epoch, commitPointReached: true)).Code);
    }

    private static User CreateUser(Role role, string name)
    {
        return new User(Guid.NewGuid(), name, name, role, Epoch);
    }

    private static JavaRuntime CreateRuntime()
    {
        return new JavaRuntime(RuntimeId, NodeId, "java.exe", "25", "OpenJDK", JavaArchitecture.X64, JavaRuntimeSource.Manual);
    }

    private static Instance CreateInstance()
    {
        JavaRuntime runtime = CreateRuntime();
        var launch = LaunchConfiguration.CreateStructured(
            runtime, 512, 1024, [], LaunchTargetKind.Jar, "server.jar", ["nogui"]);
        return new Instance(
            Guid.NewGuid(),
            NodeId,
            "Synthetic",
            new WorkingDirectory("D:\\ServitiumTests\\instance", true),
            launch,
            StopConfiguration.Default,
            Epoch);
    }

    private static ScheduledTask CreateTask(Guid instanceId, string name, IEnumerable<ScheduleAction> actions, int remainingRuns)
    {
        return new ScheduledTask(
            Guid.NewGuid(),
            instanceId,
            name,
            new IntervalTrigger(TimeSpan.FromSeconds(10)),
            actions,
            remainingRuns,
            OverlapPolicy.Skip,
            enabled: true);
    }

    private static HmclReleaseFile CreateReleaseFile(string path, char hashCharacter)
    {
        return new HmclReleaseFile(path, new ContentHash(new string(hashCharacter, 40)), 10);
    }

    private static HmclRelease CreateRelease(IEnumerable<HmclReleaseFile> files)
    {
        return new HmclRelease(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            "Synthetic",
            "Tester",
            "1.0.0",
            "Synthetic release",
            "1.21.1",
            LoaderDeclaration.None,
            files,
            Epoch,
            Guid.NewGuid());
    }
}
