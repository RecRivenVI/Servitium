using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Servitium.Client.Http;

/// <summary>把服务端 Problem Details 转为携带稳定错误码与追踪号的异常。</summary>
public static class ProblemDetailsMapper
{
    /// <summary>读取失败响应；响应正文不包含可识别 Problem Details 时使用通用错误码。</summary>
    public static async Task<ServitiumApiException> MapAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        string code = "http-error";
        string? traceId = null;
        Dictionary<string, string> parameters = new(StringComparer.Ordinal);
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (text.Length > 0)
            {
                using var document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("code", out JsonElement codeElement))
                {
                    code = codeElement.GetString() ?? code;
                }

                if (root.TryGetProperty("traceId", out JsonElement traceElement))
                {
                    traceId = traceElement.GetString();
                }

                if (root.TryGetProperty("parameters", out JsonElement parametersElement) && parametersElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in parametersElement.EnumerateObject())
                    {
                        parameters[property.Name] = property.Value.ToString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // 非 JSON 错误正文不回显，避免把代理或上游的敏感内容带进界面与日志。
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        return new ServitiumApiException(response.StatusCode, code, traceId, parameters, retryAfter);
    }
}
