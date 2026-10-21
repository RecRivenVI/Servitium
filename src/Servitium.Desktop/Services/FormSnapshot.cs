using System.Text.Json;
using System.Text.Json.Serialization;

namespace Servitium.Desktop.Services;

internal static class FormSnapshot
{
    // NumberBox 在清空输入时使用 NaN；无效表单也必须能够比较修改状态。
    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    internal static string Capture<T>(T value) { return JsonSerializer.Serialize(value, Options); }
}
