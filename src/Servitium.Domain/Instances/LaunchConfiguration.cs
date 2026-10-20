using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Domain.Primitives;
using Servitium.Domain.Java;

namespace Servitium.Domain.Instances;

/// <summary>定义启动配置模式。</summary>
public enum LaunchMode
{
    /// <summary>结构化向导。</summary>
    Structured,
    /// <summary>受限制的原始参数数组。</summary>
    Raw,
}

/// <summary>定义结构化启动目标。</summary>
public enum LaunchTargetKind
{
    /// <summary>通过 `-jar` 启动。</summary>
    Jar,
    /// <summary>通过 Java 参数文件启动。</summary>
    ArgumentFile,
}

/// <summary>
/// 表示无需 shell 的 Java 启动参数数组。
/// </summary>
public sealed class LaunchConfiguration
{
    private readonly string[] _arguments;

    private LaunchConfiguration(LaunchMode mode, Guid javaRuntimeId, string javaExecutablePath, IEnumerable<string> arguments)
    {
        Mode = mode;
        JavaRuntimeId = Guard.AgainstEmpty(javaRuntimeId, nameof(javaRuntimeId));
        JavaExecutablePath = Guard.AgainstNullOrWhiteSpace(javaExecutablePath, nameof(javaExecutablePath));
        _arguments = [.. arguments.Select(ValidateArgument)];
    }

    /// <summary>获取配置模式。</summary>
    public LaunchMode Mode { get; }

    /// <summary>获取已登记 Java 运行时标识。</summary>
    public Guid JavaRuntimeId { get; }

    /// <summary>获取已登记 java.exe 路径。</summary>
    public string JavaExecutablePath { get; }

    /// <summary>获取传给进程启动器的完整参数数组，第一项为 java.exe。</summary>
    public IReadOnlyList<string> Arguments => Array.AsReadOnly(_arguments);

    /// <summary>创建结构化启动配置。</summary>
    public static LaunchConfiguration CreateStructured(
        JavaRuntime runtime,
        int minimumHeapMegabytes,
        int maximumHeapMegabytes,
        IEnumerable<string> additionalJvmArguments,
        LaunchTargetKind targetKind,
        string target,
        IEnumerable<string> serverArguments)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(additionalJvmArguments);
        ArgumentNullException.ThrowIfNull(serverArguments);
        if (minimumHeapMegabytes <= 0 || maximumHeapMegabytes <= 0 || minimumHeapMegabytes > maximumHeapMegabytes)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumHeapMegabytes), "Heap sizes must be positive and minimum cannot exceed maximum.");
        }

        string validatedTarget = ValidateArgument(target);
        List<string> arguments =
        [
            runtime.ExecutablePath,
            $"-Xms{minimumHeapMegabytes}m",
            $"-Xmx{maximumHeapMegabytes}m",
        ];
        arguments.AddRange(additionalJvmArguments.Select(ValidateArgument));
        if (targetKind == LaunchTargetKind.Jar)
        {
            arguments.Add("-jar");
            arguments.Add(validatedTarget);
        }
        else
        {
            arguments.Add($"@{validatedTarget}");
        }

        arguments.AddRange(serverArguments.Select(ValidateArgument));
        return new LaunchConfiguration(LaunchMode.Structured, runtime.Id, runtime.ExecutablePath, arguments);
    }

    /// <summary>创建经过已登记 Java 运行时校验的原始参数数组。</summary>
    public static LaunchConfiguration CreateRaw(
        IEnumerable<string> command,
        IEnumerable<JavaRuntime> registeredRuntimes)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(registeredRuntimes);
        string[] arguments = [.. command.Select(ValidateArgument)];
        if (arguments.Length < 2)
        {
            throw new DomainException("raw-command-invalid", "The raw command must include a registered java.exe and at least one argument.");
        }

        JavaRuntime? runtime = registeredRuntimes.FirstOrDefault(candidate =>
            string.Equals(candidate.ExecutablePath, arguments[0], StringComparison.OrdinalIgnoreCase)) ?? throw new DomainException("raw-command-invalid", "The first raw command argument must be a registered java.exe.");
        return new LaunchConfiguration(LaunchMode.Raw, runtime.Id, runtime.ExecutablePath, arguments);
    }

    private static string ValidateArgument(string? argument)
    {
        string value = Guard.AgainstNullOrWhiteSpace(argument, nameof(argument));
        return Guard.AgainstControlCharacters(value, nameof(argument));
    }
}
