using System;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Contracts.Instances;
using Servitium.Contracts.Transport;

namespace Servitium.Client;

public sealed partial class ServitiumApiClient
{
    /// <summary>分页读取实例最近 24 小时的每分钟真实指标样本。</summary>
    public Task<CursorPage<InstanceMetricSampleDto>> ListInstanceMetricHistoryAsync(Guid instanceId, DateTimeOffset? since,
        string? cursor, int limit, CancellationToken cancellationToken)
    {
        return GetPageAsync<InstanceMetricSampleDto>(
            $"api/v1/instances/{instanceId:D}/metrics/history?since={Date(since)}&cursor={Escape(cursor)}&limit={limit.ToString(System.Globalization.CultureInfo.InvariantCulture)}", cancellationToken);
    }
}
