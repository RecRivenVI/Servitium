using System;

namespace Servitium.Contracts.Instances;

/// <summary>实例指标历史的真实采样点；每分钟保留最后一个样本。</summary>
public sealed record InstanceMetricSampleDto(DateTimeOffset SampledAt, double CpuPercent, long MemoryBytes, int? OnlinePlayers);
