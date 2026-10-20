using System;

namespace Servitium.Client.Http;

/// <summary>已完整读取的小型下载结果。</summary>
public sealed record DownloadedFile(string FileName, string? MediaType, ReadOnlyMemory<byte> Content);

/// <summary>已流式写入调用方目标的下载元数据。</summary>
public sealed record DownloadedFileInfo(string FileName, string? MediaType, long? Length);
