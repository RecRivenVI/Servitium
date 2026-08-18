using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Client.Http;

/// <summary>只对幂等读取的瞬时网络错误与 5xx 响应执行有界重试。</summary>
public sealed class RetryPolicy
{
    private static readonly TimeSpan[] Delays = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(300)];

    /// <summary>发送由工厂创建的新请求；请求实例不会跨重试复用。</summary>
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Func<HttpRequestMessage> requestFactory,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(requestFactory);
        for (int attempt = 0; ; attempt++)
        {
            using HttpRequestMessage request = requestFactory();
            bool retryable = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
            try
            {
                HttpResponseMessage response = await client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
                response.RequestMessage ??= request;
                if (!retryable || (int)response.StatusCode < 500 || attempt >= Delays.Length)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (HttpRequestException) when (retryable && attempt < Delays.Length)
            {
            }

            await Task.Delay(Delays[attempt], cancellationToken).ConfigureAwait(false);
        }
    }
}
