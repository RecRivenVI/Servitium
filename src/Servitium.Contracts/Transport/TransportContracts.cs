using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Servitium.Contracts.Transport;

/// <summary>游标分页请求。</summary>
/// <param name="Cursor">不透明游标。</param>
/// <param name="PageSize">页大小。</param>
public sealed record CursorPageRequest(string? Cursor = null, int PageSize = 50);

/// <summary>游标分页响应。</summary>
/// <typeparam name="T">项目类型。</typeparam>
/// <param name="Items">当前页项目。</param>
/// <param name="NextCursor">下一页游标。</param>
public sealed record CursorPage<T>(IReadOnlyList<T> Items, string? NextCursor);

/// <summary>长操作接受响应。</summary>
/// <param name="OperationId">长操作标识。</param>
public sealed record OperationAcceptedResponse(Guid OperationId);

/// <summary>稳定错误响应的扩展字段。</summary>
/// <param name="Code">稳定错误码。</param>
/// <param name="TraceId">追踪标识。</param>
/// <param name="Parameters">供客户端本地化的结构化参数。</param>
public sealed record ErrorExtension(string Code, string TraceId, IReadOnlyDictionary<string, string> Parameters);

/// <summary>REST 接口统一返回的 RFC 7807 Problem Details。</summary>
/// <param name="Type">稳定错误类型 URI。</param>
/// <param name="Title">固定为 en-US 的短标题。</param>
/// <param name="Status">HTTP 状态码。</param>
/// <param name="Detail">固定为 en-US 的诊断说明。</param>
/// <param name="Instance">发生错误的请求路径。</param>
/// <param name="Code">供客户端分支和本地化的稳定错误码。</param>
/// <param name="TraceId">服务端追踪标识。</param>
/// <param name="Parameters">供客户端本地化使用的非秘密参数。</param>
/// <param name="Errors">仅校验失败时返回的字段错误。</param>
public sealed record ProblemDetailsDto(
    string Type,
    string Title,
    int Status,
    string Detail,
    string Instance,
    string Code,
    string TraceId,
    IReadOnlyDictionary<string, string> Parameters,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Errors = null);
