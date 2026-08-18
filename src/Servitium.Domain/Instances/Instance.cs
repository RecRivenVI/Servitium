using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Domain.Common;

namespace Servitium.Domain.Instances;

/// <summary>定义实例运行状态。</summary>
public enum InstanceStatus
{
    /// <summary>已停止。</summary>
    Stopped,
    /// <summary>启动中。</summary>
    Starting,
    /// <summary>运行中。</summary>
    Running,
    /// <summary>停止中。</summary>
    Stopping,
    /// <summary>长操作占用中。</summary>
    Busy,
    /// <summary>异常退出。</summary>
    Crashed,
    /// <summary>节点不可达。</summary>
    Unreachable,
}

/// <summary>表示非法实例状态转换。</summary>
/// <remarks>初始化状态转换异常。</remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1032:实现标准异常构造函数",
    Justification = "状态转换异常必须始终携带当前状态、转换名和稳定错误码。")]
public sealed class InvalidInstanceStateTransitionException(InstanceStatus current, string transition, string code) : DomainException(code, $"Cannot apply transition '{transition}' while instance status is '{current}'.")
{

    /// <summary>获取转换前状态。</summary>
    public InstanceStatus Current { get; } = current;

    /// <summary>获取转换名称。</summary>
    public string Transition { get; } = transition;
}

/// <summary>表示 Java 服务端实例。</summary>
/// <remarks>初始化实例。</remarks>
public sealed class Instance(
    Guid id,
    Guid nodeId,
    string name,
    WorkingDirectory workingDirectory,
    LaunchConfiguration launchConfiguration,
    StopConfiguration stopConfiguration,
    DateTimeOffset createdAt) : Entity(id)
{
    private readonly HashSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private InstanceStatus? _busyReturnStatus;

    /// <summary>获取所属节点标识。</summary>
    public Guid NodeId { get; } = Guard.AgainstEmpty(nodeId, nameof(nodeId));

    /// <summary>获取名称。</summary>
    public string Name { get; private set; } = ValidateName(name);

    /// <summary>获取工作目录。</summary>
    public WorkingDirectory WorkingDirectory { get; private set; } = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));

    /// <summary>获取启动配置。</summary>
    public LaunchConfiguration LaunchConfiguration { get; private set; } = launchConfiguration ?? throw new ArgumentNullException(nameof(launchConfiguration));

    /// <summary>获取停止配置。</summary>
    public StopConfiguration StopConfiguration { get; private set; } = stopConfiguration ?? throw new ArgumentNullException(nameof(stopConfiguration));

    /// <summary>获取当前状态。</summary>
    public InstanceStatus Status { get; private set; } = InstanceStatus.Stopped;

    /// <summary>获取创建时间。</summary>
    public DateTimeOffset CreatedAt { get; } = Guard.AsUtc(createdAt);

    /// <summary>获取标签只读快照。</summary>
    public IReadOnlySet<string> Tags => new HashSet<string>(_tags, StringComparer.OrdinalIgnoreCase);

    /// <summary>获取备注。</summary>
    public string? Notes { get; private set; }

    /// <summary>进入启动中。</summary>
    public void Start()
    {
        TransitionFrom(InstanceStatus.Stopped, InstanceStatus.Starting, "start", "instance-not-stopped");
    }

    /// <summary>标记启动成功。</summary>
    public void MarkStarted()
    {
        TransitionFrom(InstanceStatus.Starting, InstanceStatus.Running, "start-succeeded", "conflict");
    }

    /// <summary>标记启动失败。</summary>
    public void MarkStartFailed(bool processCrashed)
    {
        TransitionFrom(
            InstanceStatus.Starting,
            processCrashed ? InstanceStatus.Crashed : InstanceStatus.Stopped,
            "start-failed",
            "conflict");
    }

    /// <summary>进入停止中。</summary>
    public void Stop()
    {
        TransitionFrom(InstanceStatus.Running, InstanceStatus.Stopping, "stop", "conflict");
    }

    /// <summary>标记正常停止。</summary>
    public void MarkStopped()
    {
        TransitionFrom(InstanceStatus.Stopping, InstanceStatus.Stopped, "stopped", "conflict");
    }

    /// <summary>标记运行进程异常退出。</summary>
    public void MarkCrashed()
    {
        TransitionFrom(InstanceStatus.Running, InstanceStatus.Crashed, "process-exited", "conflict");
    }

    /// <summary>从崩溃状态进入自动重启。</summary>
    public void BeginAutomaticRestart()
    {
        TransitionFrom(InstanceStatus.Crashed, InstanceStatus.Starting, "automatic-restart", "conflict");
    }

    /// <summary>从任意状态强制回到停止状态。</summary>
    public void ForceStop()
    {
        _busyReturnStatus = null;
        Status = InstanceStatus.Stopped;
    }

    /// <summary>为长操作暂存当前状态并进入 Busy。</summary>
    public void BeginBusyOperation()
    {
        if (Status is InstanceStatus.Busy or InstanceStatus.Unreachable)
        {
            throw Invalid("begin-busy", "conflict");
        }

        _busyReturnStatus = Status;
        Status = InstanceStatus.Busy;
    }

    /// <summary>完成长操作并恢复先前状态。</summary>
    public void CompleteBusyOperation()
    {
        if (Status != InstanceStatus.Busy || _busyReturnStatus is null)
        {
            throw Invalid("complete-busy", "conflict");
        }

        Status = _busyReturnStatus.Value;
        _busyReturnStatus = null;
    }

    /// <summary>标记所属节点不可达。</summary>
    public void MarkUnreachable()
    {
        if (Status == InstanceStatus.Unreachable)
        {
            throw Invalid("mark-unreachable", "conflict");
        }

        Status = InstanceStatus.Unreachable;
        _busyReturnStatus = null;
    }

    /// <summary>节点恢复后使用 Agent 报告的实际状态。</summary>
    public void RestoreReachable(InstanceStatus actualStatus)
    {
        if (Status != InstanceStatus.Unreachable || actualStatus == InstanceStatus.Unreachable)
        {
            throw Invalid("restore-reachable", "conflict");
        }

        Status = actualStatus;
    }

    /// <summary>仅在停止状态修改工作目录。</summary>
    public void ChangeWorkingDirectory(WorkingDirectory workingDirectory)
    {
        if (Status != InstanceStatus.Stopped)
        {
            throw Invalid("change-working-directory", "instance-not-stopped");
        }

        WorkingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
    }

    /// <summary>更新名称、标签与备注。</summary>
    public void UpdateMetadata(string name, IEnumerable<string> tags, string? notes)
    {
        Name = ValidateName(name);
        ArgumentNullException.ThrowIfNull(tags);
        _tags.Clear();
        foreach (string tag in tags.Select(ValidateTag))
        {
            _ = _tags.Add(tag);
        }

        Notes = string.IsNullOrWhiteSpace(notes)
            ? null
            : Guard.AgainstControlCharacters(notes.Trim(), nameof(notes));
    }

    private static string ValidateName(string name)
    {
        string value = Guard.AgainstControlCharacters(
            Guard.AgainstNullOrWhiteSpace(name, nameof(name)),
            nameof(name));
        return value.Length > 64
            ? throw new ArgumentOutOfRangeException(nameof(name), "The instance name cannot exceed 64 characters.")
            : value;
    }

    private static string ValidateTag(string tag)
    {
        return Guard.AgainstControlCharacters(Guard.AgainstNullOrWhiteSpace(tag, nameof(tag)), nameof(tag));
    }

    private void TransitionFrom(InstanceStatus required, InstanceStatus target, string transition, string code)
    {
        if (Status != required)
        {
            throw Invalid(transition, code);
        }

        Status = target;
    }

    private InvalidInstanceStateTransitionException Invalid(string transition, string code)
    {
        return new InvalidInstanceStateTransitionException(Status, transition, code);
    }
}

/// <summary>停止序列配置。</summary>
/// <param name="Command">写入标准输入的停止命令。</param>
/// <param name="TimeoutSeconds">等待正常停止的秒数。</param>
public sealed record StopConfiguration(string Command, int TimeoutSeconds) : ValueObject
{
    /// <summary>默认停止配置。</summary>
    public static StopConfiguration Default { get; } = new("stop", 60);
}

/// <summary>自动启动与崩溃恢复策略。</summary>
/// <param name="StartWithAgent">是否随 Agent 启动。</param>
/// <param name="RestartAfterCrash">是否在崩溃后重启。</param>
/// <param name="MaximumRestartAttempts">最大重启次数。</param>
public sealed record AutoStartPolicy(bool StartWithAgent, bool RestartAfterCrash, int MaximumRestartAttempts) : ValueObject;

/// <summary>终端行为设置。</summary>
/// <param name="EncodingName">输出编码。</param>
/// <param name="AppendNewLine">输入时是否追加换行。</param>
/// <param name="RenderAnsi">是否渲染 ANSI。</param>
public sealed record TerminalSettings(string EncodingName, bool AppendNewLine, bool RenderAnsi) : ValueObject;

/// <summary>RCON 设置。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="Port">端口。</param>
/// <param name="ProtectedPasswordReference">受保护的密码引用。</param>
public sealed record RconSettings(bool Enabled, int Port, string? ProtectedPasswordReference) : ValueObject;

/// <summary>服务器查询设置。</summary>
/// <param name="Enabled">是否启用。</param>
/// <param name="Port">端口。</param>
public sealed record QuerySettings(bool Enabled, int Port) : ValueObject;
