using System;

namespace Servitium.Contracts.Instances;

/// <summary>从实际启动目标读取的软件元数据；未知值保持为空。</summary>
/// <param name="GameVersion">明确记录的游戏版本。</param>
/// <param name="Loader">已识别的服务端或加载器名称。</param>
/// <param name="LoaderVersion">明确记录的加载器版本。</param>
/// <param name="Source">识别所用格式，不包含本机路径。</param>
/// <param name="Issue">无法完整识别的原因代码。</param>
/// <param name="ObservedAt">实际读取时间；离线镜像可能较旧。</param>
public sealed record InstanceSoftwareDto(
    string? GameVersion,
    string? Loader,
    string? LoaderVersion,
    string Source,
    string? Issue,
    DateTimeOffset ObservedAt);
