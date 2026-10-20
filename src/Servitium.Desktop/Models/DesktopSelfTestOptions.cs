using System;
using System.IO;

namespace Servitium.Desktop.Models;

/// <summary>解析进程级自测或隔离交互测试使用的 Desktop 数据目录。</summary>
internal static class DesktopSelfTestOptions
{
    internal static string? ResolveDataDirectory(bool selfTest, string? configuredDirectory)
    {
        return !selfTest || string.IsNullOrWhiteSpace(configuredDirectory)
            ? null
            : !Path.IsPathFullyQualified(configuredDirectory)
            ? throw new InvalidOperationException("The Desktop self-test data root must be an absolute path.")
            : Path.GetFullPath(configuredDirectory);
    }
}
