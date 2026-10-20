using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

using Servitium.Contracts.Transport;

namespace Servitium.Application.Results;

/// <summary>游标中的稳定排序位置。</summary>
/// <param name="SortKey">排序键。</param>
/// <param name="Id">标识。</param>
public sealed record CursorPosition(string SortKey, Guid Id);

/// <summary>用于没有 Guid 标识的集合的稳定排序位置。</summary>
/// <param name="SortKey">主排序键。</param>
/// <param name="TieBreaker">稳定且唯一的次排序键。</param>
public sealed record StringCursorPosition(string SortKey, string TieBreaker);

/// <summary>编码和解码不透明游标。</summary>
public static class CursorCodec
{
    /// <summary>编码游标。</summary>
    public static string Encode(CursorPosition position)
    {
        ArgumentNullException.ThrowIfNull(position);
        string json = JsonSerializer.Serialize(position);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    /// <summary>解码游标。</summary>
    public static CursorPosition Decode(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        string base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            return JsonSerializer.Deserialize<CursorPosition>(Convert.FromBase64String(base64))
                ?? throw new FormatException("The cursor payload is empty.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new FormatException("The pagination cursor is invalid.", exception);
        }
    }
}

/// <summary>把数据库递增序号编码为不透明游标。</summary>
public static class SequenceCursorCodec
{
    private const byte Version = 1;

    /// <summary>编码正序数据库序号。</summary>
    public static string Encode(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);

        byte[] payload = new byte[9];
        payload[0] = Version;
        BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(1), sequence);
        return Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>解码并验证数据库序号游标。</summary>
    public static long Decode(string cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        try
        {
            string base64 = cursor.Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
            byte[] payload = Convert.FromBase64String(base64);
            long sequence = payload.Length == 9 && payload[0] == Version
                ? BinaryPrimitives.ReadInt64BigEndian(payload.AsSpan(1))
                : 0;
            return sequence > 0 ? sequence : throw new FormatException("The pagination cursor is invalid.");
        }
        catch (FormatException exception)
        {
            throw new FormatException("The pagination cursor is invalid.", exception);
        }
    }
}

/// <summary>对稳定复合键执行游标分页。</summary>
public static class CursorPaginator
{
    /// <summary>从已按键排序的来源中返回一页。</summary>
    public static CursorPage<T> Page<T>(
        IEnumerable<T> source,
        Func<T, string> sortKey,
        Func<T, Guid> id,
        string? cursor,
        int pageSize = 50)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sortKey);
        ArgumentNullException.ThrowIfNull(id);
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        CursorPosition? position = string.IsNullOrWhiteSpace(cursor) ? null : CursorCodec.Decode(cursor);
        IEnumerable<T> ordered = source
            .OrderBy(sortKey, StringComparer.Ordinal)
            .ThenBy(id);
        if (position is not null)
        {
            ordered = ordered.Where(item =>
                string.Compare(sortKey(item), position.SortKey, StringComparison.Ordinal) > 0 ||
                (string.Equals(sortKey(item), position.SortKey, StringComparison.Ordinal) && id(item).CompareTo(position.Id) > 0));
        }

        List<T> candidates = [.. ordered.Take(pageSize + 1)];
        bool hasMore = candidates.Count > pageSize;
        if (hasMore)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        string? nextCursor = hasMore && candidates.Count > 0
            ? CursorCodec.Encode(new CursorPosition(sortKey(candidates[^1]), id(candidates[^1])))
            : null;
        return new CursorPage<T>(candidates, nextCursor);
    }

    /// <summary>对字符串复合键执行游标分页。</summary>
    public static CursorPage<T> Page<T>(
        IEnumerable<T> source,
        Func<T, string> sortKey,
        Func<T, string> tieBreaker,
        string? cursor,
        int pageSize = 50)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(sortKey);
        ArgumentNullException.ThrowIfNull(tieBreaker);
        if (pageSize is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        StringCursorPosition? position = string.IsNullOrWhiteSpace(cursor) ? null : DecodeStringPosition(cursor);
        IEnumerable<T> ordered = source.OrderBy(sortKey, StringComparer.Ordinal).ThenBy(tieBreaker, StringComparer.Ordinal);
        if (position is not null)
        {
            ordered = ordered.Where(item =>
                string.Compare(sortKey(item), position.SortKey, StringComparison.Ordinal) > 0 ||
                (string.Equals(sortKey(item), position.SortKey, StringComparison.Ordinal) &&
                 string.Compare(tieBreaker(item), position.TieBreaker, StringComparison.Ordinal) > 0));
        }

        List<T> candidates = [.. ordered.Take(pageSize + 1)];
        bool hasMore = candidates.Count > pageSize;
        if (hasMore)
        {
            candidates.RemoveAt(candidates.Count - 1);
        }

        string? nextCursor = hasMore && candidates.Count > 0
            ? EncodeStringPosition(new StringCursorPosition(sortKey(candidates[^1]), tieBreaker(candidates[^1])))
            : null;
        return new CursorPage<T>(candidates, nextCursor);
    }

    private static string EncodeStringPosition(StringCursorPosition position)
    {
        string json = JsonSerializer.Serialize(position);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static StringCursorPosition DecodeStringPosition(string cursor)
    {
        string base64 = cursor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        try
        {
            return JsonSerializer.Deserialize<StringCursorPosition>(Convert.FromBase64String(base64))
                ?? throw new FormatException("The cursor payload is empty.");
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new FormatException("The pagination cursor is invalid.", exception);
        }
    }
}
