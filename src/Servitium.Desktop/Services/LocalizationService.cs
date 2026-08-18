using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

using Microsoft.Windows.ApplicationModel.Resources;

namespace Servitium.Desktop.Services;

internal sealed class LocalizationService
{
    private static readonly IReadOnlyDictionary<string, string> EmptyFallback = new Dictionary<string, string>(StringComparer.Ordinal);
    private ResourceLoader? _loader = CreateLoader();
    private IReadOnlyDictionary<string, string> _fallback = EmptyFallback;

    internal string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        try
        {
            string value = _loader?.GetString(key) ?? string.Empty;
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }
        catch (COMException)
        {
            // 未打包的 Windows App SDK 进程可能没有 ResourceLoader 的默认资源上下文。
        }
        catch (InvalidOperationException)
        {
            // 资源上下文尚未建立时使用共享 JSON 资源，避免启动失败。
        }

        return _fallback.TryGetValue(key, out string? fallback) ? fallback : key;
    }

    internal string Format(string key, params object?[] arguments)
    {
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key), arguments);
    }

    internal void Reload(string? language = null)
    {
        _loader = CreateLoader();
        _fallback = LoadFallback(language);
    }

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (COMException)
        {
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadFallback(string? language)
    {
        string culture = string.Equals(language, "zh-Hans", StringComparison.OrdinalIgnoreCase) ? "zh-Hans" : "en-US";
        string path = Path.Combine(AppContext.BaseDirectory, "Localization", culture + ".json");
        try
        {
            using FileStream stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? EmptyFallback;
        }
        catch (FileNotFoundException)
        {
            return EmptyFallback;
        }
        catch (JsonException)
        {
            return EmptyFallback;
        }
    }
}
