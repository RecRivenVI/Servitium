using System;

using Servitium.Contracts.Events;
using Servitium.Contracts.Instances;

namespace Servitium.Client.Realtime;

/// <summary>承载 Server 推送的实时事件。</summary>
/// <remarks>创建事件参数。</remarks>
public sealed class RealtimeEventArgs(RealtimeEventDto value) : EventArgs
{

    /// <summary>实时事件。</summary>
    public RealtimeEventDto Value { get; } = value;
}

/// <summary>承载实时连接状态。</summary>
/// <remarks>创建事件参数。</remarks>
public sealed class ConnectionStateChangedEventArgs(string state) : EventArgs
{

    /// <summary>稳定的连接状态代码。</summary>
    public string State { get; } = state;
}

/// <summary>承载终端输出帧。</summary>
/// <remarks>创建事件参数。</remarks>
public sealed class TerminalOutputEventArgs(TerminalOutputFrame value) : EventArgs
{

    /// <summary>输出帧。</summary>
    public TerminalOutputFrame Value { get; } = value;
}
