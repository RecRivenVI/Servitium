using System.Collections.Generic;
using System.Text;

using Servitium.Agent.Configurations;
using Servitium.Domain.Common;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class PreservingStructuredDocumentTests
{
    public static TheoryData<string, string> RoundTripSamples => new()
    {
        { "json", /*lang=json,strict*/ "{\r\n  \"name\": \"合成\",\r\n  \"number\": 1e3\r\n}\r\n" },
        { "json", /*lang=json,strict*/ "{\"a\":1,\"b\":[true,false,null]}" },
        { "json", /*lang=json,strict*/ "{\n\t\"nested\": {\n\t\t\"value\": -0.00\n\t}\n}\n" },
        { "json", /*lang=json*/ "{\n  // comment\n  \"enabled\": true,\n}\n" },
        { "json", /*lang=json,strict*/ "{\"escaped\":\"a\\\\b\\\"c\"}" },
        { "json", /*lang=json,strict*/ "{\"unicode\":\"中文\",\"n\":9007199254740993}" },
        { "json", /*lang=json,strict*/ "{\"empty\":{},\"items\":[]}" },
        { "json", /*lang=json,strict*/ "{\r\n    \"x\" : null\r\n}" },
        { "json", /*lang=json,strict*/ "{\"one\":{\"value\":1},\"two\":{\"value\":2}}" },
        { "json", /*lang=json,strict*/ "{\n\"decimal\": 0.12345678901234567890\n}\n" },
        { "yaml", "# 中文注释\r\nserver:\r\n  motd: '合成服务器'\r\n  port: 25565\r\n" },
        { "yaml", "defaults: &defaults\n  enabled: true\nserver:\n  <<: *defaults\n" },
        { "yaml", "quoted: \"a:b # c\"\nplain: value # keep\n" },
        { "yaml", "list:\n  - one\n  - two\n" },
        { "yaml", "literal: |\n  first\n  second\n" },
        { "yaml", "folded: >-\n  first\n  second\n" },
        { "yaml", "flow: { one: 1, two: [2, 3] }\n" },
        { "yaml", "---\nvalue: null\n...\n" },
        { "yaml", "single: 'it''s preserved'\n" },
        { "yaml", "nested:\n    mixed_indent: true\n" },
        { "toml", "# 中文注释\r\ntitle = \"合成\"\r\n[server]\r\nport = 25565\r\n" },
        { "toml", "number = 1_000\nhex = 0xDEAD_BEEF\n" },
        { "toml", "array = [1, 2, 3]\ninline = { a = 1, b = 2 }\n" },
        { "toml", "multiline = \"\"\"first\nsecond\"\"\"\n" },
        { "toml", "literal = '''first\nsecond'''\n" },
        { "toml", "date = 2026-08-03T12:34:56Z\n" },
        { "toml", "[a.b]\nvalue = true # keep\n" },
        { "toml", "[[servers]]\nname = \"one\"\n[[servers]]\nname = \"two\"\n" },
        { "toml", "\"quoted.key\" = \"value\"\n" },
        { "toml", "unicode = \"繁體 日本語\"\n" },
    };

    [Theory]
    [MemberData(nameof(RoundTripSamples))]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-900")]
    public void 结构化配置_未修改时逐字节往返(string format, string content)
    {
        byte[] original = Encoding.UTF8.GetBytes(content);
        var document = PreservingStructuredDocument.Parse(format, original);

        Assert.Equal(original, document.Write());
        Assert.True(document.IsUnchanged);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-910")]
    public void Json_按完整路径只修改目标字面量()
    {
        const string original = /*lang=json,strict*/ "{\"one\":{\"value\":1},\"two\":{\"value\":2},\"name\":\"保留\"}\r\n";
        var document = PreservingStructuredDocument.Parse("json", Encoding.UTF8.GetBytes(original));

        document.SetValues(new Dictionary<string, string?> { ["one.value"] = "1e3" });

        Assert.Equal(/*lang=json,strict*/ "{\"one\":{\"value\":1e3},\"two\":{\"value\":2},\"name\":\"保留\"}\r\n", Encoding.UTF8.GetString(document.Write()));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-910")]
    public void Yaml与Toml_修改标量时保留注释缩进和引号风格()
    {
        var yaml = PreservingStructuredDocument.Parse(
            "yaml",
            Encoding.UTF8.GetBytes("server:\r\n  motd : '旧值' # keep\r\nother: &anchor value\r\ncopy: *anchor\r\n"));
        yaml.SetValues(new Dictionary<string, string?> { ["server.motd"] = "新值" });
        Assert.Equal("server:\r\n  motd : '新值' # keep\r\nother: &anchor value\r\ncopy: *anchor\r\n", Encoding.UTF8.GetString(yaml.Write()));

        var toml = PreservingStructuredDocument.Parse(
            "toml",
            Encoding.UTF8.GetBytes("[server]\r\nport    = 25565 # keep\r\nname = \"旧值\"\r\n"));
        toml.SetValues(new Dictionary<string, string?> { ["server.name"] = "新值" });
        Assert.Equal("[server]\r\nport    = 25565 # keep\r\nname = \"新值\"\r\n", Encoding.UTF8.GetString(toml.Write()));
    }

    [Theory]
    [InlineData("json", "{\"a\":}")]
    [InlineData("yaml", "key: [unterminated")]
    [InlineData("toml", "key = [1,")]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-920")]
    public void 结构化配置_拒绝非法语法并返回解析错误(string format, string content)
    {
        DomainException exception = Assert.Throws<DomainException>(() =>
            PreservingStructuredDocument.Parse(format, Encoding.UTF8.GetBytes(content)));

        Assert.Equal("config-parse-failed", exception.Code);
        Assert.Contains("syntax error", exception.Message, System.StringComparison.OrdinalIgnoreCase);
    }
}
