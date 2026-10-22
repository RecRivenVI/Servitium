using System;

using Servitium.Contracts.Instances;

namespace Servitium.Application.Nodes;

/// <summary>绑定权威节点和实例的指标样本。</summary>
public sealed record StoredInstanceMetric(Guid NodeId, Guid InstanceId, InstanceMetricSampleDto Sample);
