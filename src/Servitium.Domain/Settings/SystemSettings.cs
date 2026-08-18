using System;

namespace Servitium.Domain.Settings;

/// <summary>表示与基础设施无关的系统设置约束。</summary>
public sealed class SystemSettings
{
    /// <summary>初始化系统设置。</summary>
    public SystemSettings(string defaultLanguage, int maximumAgents, int maximumInstances, int maximumConcurrentTerminals)
    {
        if (defaultLanguage is not ("zh-Hans" or "en-US"))
        {
            throw new ArgumentException("The default language must be zh-Hans or en-US.", nameof(defaultLanguage));
        }

        if (maximumAgents is < 1 or > 8 || maximumInstances is < 1 or > 64 || maximumConcurrentTerminals is < 1 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAgents), "Capacity settings exceed the supported product limits.");
        }

        DefaultLanguage = defaultLanguage;
        MaximumAgents = maximumAgents;
        MaximumInstances = maximumInstances;
        MaximumConcurrentTerminals = maximumConcurrentTerminals;
    }

    /// <summary>获取默认语言。</summary>
    public string DefaultLanguage { get; }

    /// <summary>获取 Agent 上限。</summary>
    public int MaximumAgents { get; }

    /// <summary>获取实例上限。</summary>
    public int MaximumInstances { get; }

    /// <summary>获取并发终端上限。</summary>
    public int MaximumConcurrentTerminals { get; }
}
