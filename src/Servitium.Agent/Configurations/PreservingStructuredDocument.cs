using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

using Servitium.Domain.Common;

using Tomlyn.Parsing;
using Tomlyn.Syntax;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Servitium.Agent.Configurations;

internal sealed partial class PreservingStructuredDocument
{
    private static readonly JsonSerializerOptions ValueJsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _format;
    private readonly string _original;
    private readonly bool _hasUtf8Bom;
    private string _content;

    private PreservingStructuredDocument(string format, string content, bool hasUtf8Bom)
    {
        _format = format;
        _original = content;
        _content = content;
        _hasUtf8Bom = hasUtf8Bom;
    }

    internal static PreservingStructuredDocument Parse(string format, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        string content;
        try
        {
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            content = new UTF8Encoding(false, true).GetString(bom ? bytes.AsSpan(Encoding.UTF8.Preamble.Length) : bytes);
            PreservingStructuredDocument document = new(format.ToUpperInvariant(), content, bom);
            document.Validate(content);
            return document;
        }
        catch (DecoderFallbackException exception)
        {
            throw new DomainException("config-parse-failed", exception.Message);
        }

    }

    internal IReadOnlyDictionary<string, string?> GetValues()
    {
        return _format switch
        {
            "JSON" => ReadJsonValues(_content),
            "YAML" => ReadYamlValues(_content),
            "TOML" => ReadTomlValues(_content),
            _ => new Dictionary<string, string?>(StringComparer.Ordinal),
        };
    }

    internal void SetValues(IReadOnlyDictionary<string, string?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach ((string key, string? value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || key.Any(char.IsControl))
            {
                throw new DomainException("validation-failed", "A configuration key is invalid.");
            }

            if (GetValues().TryGetValue(key, out string? current) && string.Equals(current, value, StringComparison.Ordinal))
            {
                continue;
            }

            _content = _format switch
            {
                "JSON" => ReplaceJsonScalar(_content, key, value),
                "YAML" => ReplaceYamlScalar(_content, key, value),
                "TOML" => ReplaceTomlScalar(_content, key, value),
                _ => throw new DomainException("config-format-unsupported", "Structured editing is unavailable for this format."),
            };
        }

        Validate(_content);
    }

    internal byte[] Write()
    {
        byte[] content = new UTF8Encoding(false).GetBytes(_content);
        if (!_hasUtf8Bom)
        {
            return content;
        }

        byte[] result = new byte[Encoding.UTF8.Preamble.Length + content.Length];
        Encoding.UTF8.Preamble.CopyTo(result.AsSpan());
        content.CopyTo(result.AsSpan(Encoding.UTF8.Preamble.Length));
        return result;
    }

    internal bool IsUnchanged => string.Equals(_original, _content, StringComparison.Ordinal);

    private static Dictionary<string, string?> ReadJsonValues(string content)
    {
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        FlattenJson(document.RootElement, string.Empty, values);
        return values;
    }

    private static void FlattenJson(JsonElement element, string prefix, IDictionary<string, string?> values)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string path = prefix.Length == 0 ? property.Name : prefix + "." + property.Name;
                FlattenJson(property.Value, path, values);
            }

            return;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            values[prefix] = element.GetRawText();
            return;
        }

        values[prefix] = element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText();
    }

    private static Dictionary<string, string?> ReadYamlValues(string content)
    {
        YamlStream stream = [];
        using StringReader reader = new(content);
        stream.Load(reader);
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        foreach (YamlDocument document in stream.Documents)
        {
            FlattenYaml(document.RootNode, string.Empty, values);
        }

        return values;
    }

    private static void FlattenYaml(YamlNode node, string prefix, IDictionary<string, string?> values)
    {
        if (node is YamlMappingNode mapping)
        {
            foreach ((YamlNode rawKey, YamlNode child) in mapping.Children)
            {
                string name = (rawKey as YamlScalarNode)?.Value ?? rawKey.ToString();
                string path = prefix.Length == 0 ? name : prefix + "." + name;
                FlattenYaml(child, path, values);
            }

            return;
        }

        values[prefix] = node is YamlScalarNode scalar ? scalar.Value : node.ToString();
    }

    private static Dictionary<string, string?> ReadTomlValues(string content)
    {
        Dictionary<string, string?> values = new(StringComparer.Ordinal);
        string table = string.Empty;
        foreach (string line in SplitLines(content))
        {
            Match tableMatch = TomlTable().Match(line);
            if (tableMatch.Success)
            {
                table = tableMatch.Groups["table"].Value.Trim();
                continue;
            }

            Match valueMatch = TomlLine().Match(line);
            if (valueMatch.Success)
            {
                string key = valueMatch.Groups["key"].Value.Trim();
                values[table.Length == 0 ? key : table + "." + key] = StripComment(valueMatch.Groups["value"].Value, '#').Trim();
            }
        }

        return values;
    }

    private static string ReplaceJsonScalar(string content, string key, string? value)
    {
        string encoded = EncodeJsonValue(value);
        (int Start, int Length)? span = FindJsonValueSpan(content, key) ?? throw new DomainException("config-parse-failed", "The requested JSON key does not exist or is not a scalar value.");
        return content[..span.Value.Start] + encoded + content[(span.Value.Start + span.Value.Length)..];
    }

    private static (int Start, int Length)? FindJsonValueSpan(string content, string wantedPath)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        List<string> path = [];
        Stack<bool> pushedPath = new();
        string? pendingProperty = null;
        long captureStart = -1;
        int captureDepth = -1;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                pendingProperty = reader.GetString();
                continue;
            }

            if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                bool pushed = pendingProperty is not null;
                if (pushed)
                {
                    path.Add(pendingProperty!);
                    pendingProperty = null;
                }

                pushedPath.Push(pushed);
                if (captureStart < 0 && string.Equals(string.Join('.', path), wantedPath, StringComparison.Ordinal))
                {
                    captureStart = reader.TokenStartIndex;
                    captureDepth = reader.CurrentDepth;
                }

                continue;
            }

            if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray)
            {
                if (captureStart >= 0 && reader.CurrentDepth == captureDepth)
                {
                    return ToCharacterSpan(bytes, captureStart, reader.BytesConsumed);
                }

                if (pushedPath.Pop())
                {
                    path.RemoveAt(path.Count - 1);
                }

                continue;
            }

            if (pendingProperty is not null)
            {
                string current = path.Count == 0 ? pendingProperty : string.Join('.', path) + "." + pendingProperty;
                pendingProperty = null;
                if (string.Equals(current, wantedPath, StringComparison.Ordinal))
                {
                    return ToCharacterSpan(bytes, reader.TokenStartIndex, reader.BytesConsumed);
                }
            }
        }

        return null;
    }

    private static (int Start, int Length) ToCharacterSpan(byte[] utf8, long byteStart, long byteEnd)
    {
        int start = Encoding.UTF8.GetCharCount(utf8.AsSpan(0, checked((int)byteStart)));
        int length = Encoding.UTF8.GetCharCount(utf8.AsSpan(checked((int)byteStart), checked((int)(byteEnd - byteStart))));
        return (start, length);
    }

    private static string ReplaceYamlScalar(string content, string key, string? value)
    {
        string[] wanted = key.Split('.');
        List<(int Indent, string Key)> parents = [];
        string[] lines = SplitLinesWithEndings(content);
        for (int index = 0; index < lines.Length; index++)
        {
            string lineWithoutEnding = TrimLineEnding(lines[index], out string ending);
            Match match = YamlLine().Match(lineWithoutEnding);
            if (!match.Success)
            {
                continue;
            }

            int indent = match.Groups["indent"].Value.Length;
            while (parents.Count > 0 && parents[^1].Indent >= indent)
            {
                parents.RemoveAt(parents.Count - 1);
            }

            string currentKey = match.Groups["key"].Value.Trim().Trim('"', '\'');
            string path = string.Join('.', parents.Select(parent => parent.Key).Append(currentKey));
            string oldValue = match.Groups["value"].Value;
            if (string.Equals(path, key, StringComparison.Ordinal))
            {
                string comment = ExtractComment(oldValue, '#');
                string replacement = EncodePreservingQuote(value, StripComment(oldValue, '#').Trim());
                int valueStart = match.Groups["value"].Index;
                lines[index] = lineWithoutEnding[..valueStart] + replacement + comment + ending;
                return string.Concat(lines);
            }

            if (wanted.Length > parents.Count + 1 && string.Equals(wanted[parents.Count], currentKey, StringComparison.Ordinal))
            {
                parents.Add((indent, currentKey));
            }
        }

        throw new DomainException("config-parse-failed", "The requested YAML key does not exist.");
    }

    private static string ReplaceTomlScalar(string content, string key, string? value)
    {
        string[] parts = key.Split('.');
        string leaf = parts[^1];
        string wantedTable = parts.Length > 1 ? string.Join('.', parts[..^1]) : string.Empty;
        string currentTable = string.Empty;
        string[] lines = SplitLinesWithEndings(content);
        for (int index = 0; index < lines.Length; index++)
        {
            string lineWithoutEnding = TrimLineEnding(lines[index], out string ending);
            Match tableMatch = TomlTable().Match(lineWithoutEnding);
            if (tableMatch.Success)
            {
                currentTable = tableMatch.Groups["table"].Value.Trim();
                continue;
            }

            Match valueMatch = TomlLine().Match(lineWithoutEnding);
            if (!valueMatch.Success || !string.Equals(currentTable, wantedTable, StringComparison.Ordinal) ||
                !string.Equals(valueMatch.Groups["key"].Value.Trim(), leaf, StringComparison.Ordinal))
            {
                continue;
            }

            string oldValue = valueMatch.Groups["value"].Value;
            string comment = ExtractComment(oldValue, '#');
            int valueStart = valueMatch.Groups["value"].Index;
            lines[index] = lineWithoutEnding[..valueStart] + EncodePreservingQuote(value, StripComment(oldValue, '#').Trim()) + comment + ending;
            return string.Concat(lines);
        }

        throw new DomainException("config-parse-failed", "The requested TOML key does not exist.");
    }

    private void Validate(string content)
    {
        if (_format == "JSON")
        {
            try
            {
                using var _ = JsonDocument.Parse(content, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException exception)
            {
                throw new DomainException("config-parse-failed", FormattableString.Invariant($"JSON syntax error at line {exception.LineNumber}, byte {exception.BytePositionInLine}."));
            }

            return;
        }

        if (_format == "YAML")
        {
            try
            {
                YamlStream stream = [];
                using StringReader reader = new(content);
                stream.Load(reader);
            }
            catch (YamlException exception)
            {
                throw new DomainException(
                    "config-parse-failed",
                    FormattableString.Invariant($"YAML syntax error at line {exception.Start.Line}, column {exception.Start.Column}."));
            }

            return;
        }

        if (_format == "TOML")
        {
            try
            {
                DocumentSyntax syntax = SyntaxParser.ParseStrict(content, "configuration.toml", false);
                if (syntax.HasErrors)
                {
                    throw new DomainException("config-parse-failed", "TOML syntax error: " + string.Join("; ", syntax.Diagnostics.Select(diagnostic => diagnostic.ToString())));
                }
            }
            catch (Tomlyn.TomlException exception)
            {
                throw new DomainException("config-parse-failed", "TOML syntax error: " + exception.Message);
            }
        }
    }

    private static string EncodeJsonValue(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        string trimmed = value.Trim();
        try
        {
            using var parsed = JsonDocument.Parse(trimmed);
            return parsed.RootElement.ValueKind == JsonValueKind.String ? JsonSerializer.Serialize(value, ValueJsonOptions) : trimmed;
        }
        catch (JsonException)
        {
            return JsonSerializer.Serialize(value, ValueJsonOptions);
        }
    }

    private static string EncodePreservingQuote(string? value, string previous)
    {
        value ??= string.Empty;
        return previous.Length >= 2 && previous.StartsWith('\'') && previous.EndsWith('\'')
            ? "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'"
            : previous.Length >= 2 && previous.StartsWith('"') && previous.EndsWith('"')
            ? JsonSerializer.Serialize(value, ValueJsonOptions)
            : EncodeLineValue(value);
    }

    private static string EncodeLineValue(string? value)
    {
        return value is null
            ? ""
            : value.Any(character => char.IsWhiteSpace(character) || character is '#' or ':' or '=')
            ? JsonSerializer.Serialize(value)
            : value;
    }

    private static string StripComment(string value, char marker)
    {
        int index = FindUnquoted(value, marker);
        return index < 0 ? value : value[..index];
    }

    private static string ExtractComment(string value, char marker)
    {
        int index = FindUnquoted(value, marker);
        return index < 0 ? string.Empty : " " + value[index..].TrimStart();
    }

    private static int FindUnquoted(string value, char marker)
    {
        bool single = false;
        bool doubleQuoted = false;
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (character == '\'' && !doubleQuoted)
            {
                single = !single;
            }
            else if (character == '"' && !single && (index == 0 || value[index - 1] != '\\'))
            {
                doubleQuoted = !doubleQuoted;
            }
            else if (character == marker && !single && !doubleQuoted)
            {
                return index;
            }
        }

        return -1;
    }

    private static string[] SplitLines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private static string[] SplitLinesWithEndings(string value)
    {
        return Regex.Split(value, "(?<=\\r\\n|\\n|\\r)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string TrimLineEnding(string line, out string ending)
    {
        if (line.EndsWith("\r\n", StringComparison.Ordinal))
        {
            ending = "\r\n";
            return line[..^2];
        }

        if (line.EndsWith('\n') || line.EndsWith('\r'))
        {
            ending = line[^1].ToString();
            return line[..^1];
        }

        ending = string.Empty;
        return line;
    }

    [GeneratedRegex("^(?<indent>\\s*)(?<key>[^:#][^:]*?)\\s*:\\s*(?<value>.*)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex YamlLine();

    [GeneratedRegex("^(?<indent>\\s*)(?<key>[A-Za-z0-9_.-]+)\\s*=\\s*(?<value>.*)$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TomlLine();

    [GeneratedRegex("^\\s*\\[(?<table>[^]]+)]\\s*(?:#.*)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TomlTable();
}
