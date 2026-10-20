using System;
using System.Collections.Generic;
using System.Linq;

using Servitium.Contracts.Configurations;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class ServerPropertiesSchemaTests
{
    private static readonly string[] RequiredGroups = ["network", "gameplay", "world", "performance", "security", "other"];
    private static readonly string[] AllowedTypes = ["string", "password", "integer", "decimal", "boolean", "enum", "multiline"];

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("TestId", "UNT-340")]
    public void ServerProperties字段定义_可解析且字段完整无重复()
    {
        IReadOnlyList<ConfigurationFieldDto> fields = ServerPropertiesSchema.Fields;
        Assert.True(fields.Count >= 60);
        Assert.Equal(fields.Count, fields.Select(field => field.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (ConfigurationFieldDto field in fields)
        {
            Assert.False(string.IsNullOrWhiteSpace(field.Key));
            Assert.Contains(field.Type, AllowedTypes);
            Assert.False(string.IsNullOrWhiteSpace(field.DescriptionKey));
            Assert.Contains(field.Group, RequiredGroups);
            if (field.Type == "enum")
            {
                Assert.NotEmpty(field.AllowedValues ?? []);
            }

            if (field.Minimum is not null && field.Maximum is not null)
            {
                Assert.True(field.Minimum <= field.Maximum);
            }
        }
    }
}
