using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Servitium.Domain.Common;

namespace Servitium.Agent.Configurations;

internal sealed class PreservingPropertiesDocument
{
    private readonly List<Line> _lines;
    private readonly bool _hasUtf8Bom;
    private readonly string _defaultLineEnding;

    private PreservingPropertiesDocument(List<Line> lines, bool hasUtf8Bom, string defaultLineEnding)
    {
        _lines = lines;
        _hasUtf8Bom = hasUtf8Bom;
        _defaultLineEnding = defaultLineEnding;
    }

    internal static PreservingPropertiesDocument Parse(ReadOnlySpan<byte> bytes)
    {
        bool bom = bytes.StartsWith(Encoding.UTF8.Preamble);
        ReadOnlySpan<byte> contentBytes = bom ? bytes[Encoding.UTF8.Preamble.Length..] : bytes;
        string content;
        try
        {
            content = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(contentBytes);
        }
        catch (DecoderFallbackException)
        {
            throw new DomainException("config-parse-failed", "The properties document is not valid UTF-8.");
        }

        List<Line> lines = [];
        string? firstEnding = null;
        int position = 0;
        while (position < content.Length)
        {
            int newline = content.IndexOf('\n', position);
            if (newline < 0)
            {
                lines.Add(ParseLine(content[position..], string.Empty));
                position = content.Length;
                continue;
            }

            bool crlf = newline > position && content[newline - 1] == '\r';
            int lineEnd = crlf ? newline - 1 : newline;
            string ending = crlf ? "\r\n" : "\n";
            firstEnding ??= ending;
            lines.Add(ParseLine(content[position..lineEnd], ending));
            position = newline + 1;
        }

        if (content.Length == 0)
        {
            lines.Add(new Line(string.Empty, string.Empty, null, -1, false, false));
        }

        for (int index = 1; index < lines.Count; index++)
        {
            if (lines[index - 1].Continued)
            {
                Line current = lines[index];
                int valueStart = current.Raw.TakeWhile(char.IsWhiteSpace).Count();
                lines[index] = current with { Key = null, ValueStart = valueStart, IsContinuation = true };
            }
        }

        return new PreservingPropertiesDocument(lines, bom, firstEnding ?? Environment.NewLine);
    }

    internal IReadOnlyDictionary<string, string> GetValues()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 0; index < _lines.Count; index++)
        {
            Line line = _lines[index];
            if (line.Key is null || line.IsContinuation)
            {
                continue;
            }

            values[line.Key] = ReadLogicalValue(index);
        }

        return values;
    }

    internal string? GetValue(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return GetValues().TryGetValue(key, out string? value) ? value : null;
    }

    internal void SetValue(string key, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        if (key.Any(character => character is '=' or ':' or '\r' or '\n' || char.IsControl(character)) ||
            value.Any(character => character is '\r' or '\n' || char.IsControl(character)))
        {
            throw new DomainException("validation-failed", "The properties key or value is invalid.");
        }

        for (int index = _lines.Count - 1; index >= 0; index--)
        {
            Line line = _lines[index];
            if (!string.Equals(line.Key, key, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(ReadLogicalValue(index), value, StringComparison.Ordinal))
            {
                return;
            }

            int end = index;
            while (_lines[end].Continued && end + 1 < _lines.Count && _lines[end + 1].IsContinuation)
            {
                end++;
            }

            int lineCount = end - index + 1;
            int consumed = 0;
            for (int part = 0; part < lineCount; part++)
            {
                int currentIndex = index + part;
                Line current = _lines[currentIndex];
                int remainingLines = lineCount - part;
                int remainingCharacters = value.Length - consumed;
                int take = part == lineCount - 1
                    ? remainingCharacters
                    : Math.Max(0, (int)Math.Ceiling((double)remainingCharacters / remainingLines));
                string segment = value.Substring(consumed, take);
                consumed += take;
                bool continued = part < lineCount - 1;
                _lines[currentIndex] = current with
                {
                    Raw = current.Raw[..current.ValueStart] + segment + (continued ? "\\" : string.Empty),
                    Continued = continued,
                };
            }

            return;
        }

        if (_lines.Count > 0 && _lines[^1].Ending.Length == 0 && _lines[^1].Raw.Length > 0)
        {
            _lines[^1] = _lines[^1] with { Ending = _defaultLineEnding };
        }

        _lines.Add(new Line(key + "=" + value, _defaultLineEnding, key, key.Length + 1, false, false));
    }

    internal byte[] Write()
    {
        StringBuilder builder = new();
        foreach (Line line in _lines)
        {
            _ = builder.Append(line.Raw).Append(line.Ending);
        }

        byte[] content = new UTF8Encoding(false).GetBytes(builder.ToString());
        if (!_hasUtf8Bom)
        {
            return content;
        }

        ReadOnlySpan<byte> preamble = Encoding.UTF8.Preamble;
        byte[] result = new byte[preamble.Length + content.Length];
        preamble.CopyTo(result);
        content.CopyTo(result, preamble.Length);
        return result;
    }

    private static Line ParseLine(string raw, string ending)
    {
        int nonWhitespace = 0;
        while (nonWhitespace < raw.Length && char.IsWhiteSpace(raw[nonWhitespace]))
        {
            nonWhitespace++;
        }

        if (nonWhitespace == raw.Length || raw[nonWhitespace] is '#' or '!')
        {
            return new Line(raw, ending, null, -1, false, false);
        }

        bool escaped = false;
        int separator = -1;
        for (int index = nonWhitespace; index < raw.Length; index++)
        {
            char character = raw[index];
            if (!escaped && (character is '=' or ':' || char.IsWhiteSpace(character)))
            {
                separator = index;
                break;
            }

            escaped = !escaped && character == '\\';
            if (character != '\\')
            {
                escaped = false;
            }
        }

        string key = (separator < 0 ? raw[nonWhitespace..] : raw[nonWhitespace..separator]).Trim();
        if (key.Length == 0)
        {
            return new Line(raw, ending, null, -1, false, false);
        }

        int valueStart = separator < 0 ? raw.Length : separator;
        while (valueStart < raw.Length && char.IsWhiteSpace(raw[valueStart]))
        {
            valueStart++;
        }

        if (valueStart < raw.Length && raw[valueStart] is '=' or ':')
        {
            valueStart++;
        }

        while (valueStart < raw.Length && char.IsWhiteSpace(raw[valueStart]))
        {
            valueStart++;
        }

        int trailingBackslashes = raw.Reverse().TakeWhile(character => character == '\\').Count();
        return new Line(raw, ending, key, valueStart, trailingBackslashes % 2 == 1, false);
    }

    private string ReadLogicalValue(int index)
    {
        StringBuilder value = new();
        Line current = _lines[index];
        while (true)
        {
            string segment = current.ValueStart >= current.Raw.Length ? string.Empty : current.Raw[current.ValueStart..];
            if (current.Continued && segment.EndsWith('\\'))
            {
                segment = segment[..^1];
            }

            _ = value.Append(segment);
            if (!current.Continued || ++index >= _lines.Count || !_lines[index].IsContinuation)
            {
                return value.ToString();
            }

            current = _lines[index];
        }
    }

    private sealed record Line(string Raw, string Ending, string? Key, int ValueStart, bool Continued, bool IsContinuation);
}
