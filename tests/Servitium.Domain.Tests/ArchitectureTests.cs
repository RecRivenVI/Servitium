using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

using Servitium.Contracts.Events;
using Servitium.Domain.Auditing;

using Xunit;

namespace Servitium.Domain.Tests;

public sealed class ArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedReferences =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Servitium.Domain"] = NewSet(),
            ["Servitium.Contracts"] = NewSet(),
            ["Servitium.Application"] = NewSet("Servitium.Domain", "Servitium.Contracts"),
            ["Servitium.Infrastructure"] = NewSet("Servitium.Application", "Servitium.Domain"),
            ["Servitium.Client"] = NewSet("Servitium.Contracts"),
            ["Servitium.Server"] = NewSet("Servitium.Application", "Servitium.Contracts", "Servitium.Infrastructure"),
            ["Servitium.Agent"] = NewSet("Servitium.Application", "Servitium.Contracts", "Servitium.Infrastructure"),
        };

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-001")]
    public void Domain_项目引用_不引用其他Servitium项目()
    {
        HashSet<string> references = ReadProjectReferences("Servitium.Domain");

        Assert.Empty(references);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-002")]
    public void Domain_包引用_不包含框架与IO包()
    {
        HashSet<string> packages = ReadPackageReferences("Servitium.Domain");

        Assert.DoesNotContain(packages, package =>
            package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            package.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) ||
            package.Contains("FileSystem", StringComparison.OrdinalIgnoreCase) ||
            package.Contains("IO", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-003")]
    public void Application_项目引用_仅包含Domain与Contracts()
    {
        HashSet<string> references = ReadProjectReferences("Servitium.Application");

        Assert.True(NewSet("Servitium.Domain", "Servitium.Contracts").SetEquals(references));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-004")]
    public void Contracts_项目引用_不引用其他Servitium项目()
    {
        HashSet<string> references = ReadProjectReferences("Servitium.Contracts");

        Assert.Empty(references);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-005")]
    public void Client_项目引用_仅包含Contracts()
    {
        HashSet<string> references = ReadProjectReferences("Servitium.Client");

        Assert.True(NewSet("Servitium.Contracts").SetEquals(references));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-006")]
    public void 全部源码项目_项目引用图_符合架构基线()
    {
        foreach ((string project, HashSet<string> expected) in AllowedReferences)
        {
            HashSet<string> actual = ReadProjectReferences(project);
            Assert.True(expected.SetEquals(actual), $"Unexpected project references for {project}: {string.Join(',', actual)}");
        }
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-020")]
    public void Domain_公共字段与属性_不使用DateTime()
    {
        Assembly domain = typeof(AuditLogEntry).Assembly;
        IEnumerable<MemberInfo> invalidMembers = domain
            .GetExportedTypes()
            .SelectMany(type => type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Where(member => member switch
            {
                PropertyInfo property => property.PropertyType == typeof(DateTime),
                FieldInfo field => field.FieldType == typeof(DateTime),
                _ => false,
            });

        Assert.Empty(invalidMembers);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-021")]
    public void AuditLogEntry_没有公共Setter或更新删除方法()
    {
        Type type = typeof(AuditLogEntry);

        Assert.DoesNotContain(type.GetProperties(), property => property.SetMethod?.IsPublic == true);
        Assert.DoesNotContain(type.GetMethods(BindingFlags.Public | BindingFlags.Instance), method =>
            method.Name.StartsWith("Update", StringComparison.Ordinal) ||
            method.Name.StartsWith("Delete", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-023")]
    public void 事件类型_与协议表固定集合一致()
    {
        string[] expected =
        [
            "node.status-changed",
            "node.metrics",
            "node.credential-rotated",
            "instance.created",
            "instance.updated",
            "instance.unregistered",
            "instance.status-changed",
            "instance.resource-usage",
            "instance.crashed",
            "instance.auto-restart-exhausted",
            "instance.players-changed",
            "operation.started",
            "operation.progress",
            "operation.completed",
            "file.task-progress",
            "schedule.run-completed",
            "hmcl.release-created",
            "hmcl.published",
            "hmcl.integrity-alert",
            "audit.security-event",
            "session.revoked",
            "authorization.changed",
        ];

        Assert.True(expected.ToHashSet(StringComparer.Ordinal).SetEquals(EventTypes.All));
    }

    [Fact]
    [Trait("Category", "Architecture")]
    [Trait("TestId", "ARC-024")]
    public void 界面契约_覆盖信息架构全部页面编号()
    {
        string repositoryRoot = RepositoryRootLocator.Find();
        string contractPath = Path.Combine(repositoryRoot, "src", "Servitium.Contracts", "Ui", "ui-contract.json");
        string architecturePath = Path.Combine(repositoryRoot, "docs", "design", "information-architecture.md");
        using var contract = JsonDocument.Parse(File.ReadAllText(contractPath));
        var contractIds = contract.RootElement
            .GetProperty("pages")
            .EnumerateArray()
            .Select(page => page.GetProperty("id").GetString())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var specificationIds = File.ReadLines(architecturePath)
            .Select(line => line.Split('|', StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length > 2 && parts[1].StartsWith("PG-", StringComparison.Ordinal))
            .Select(parts => parts[1])
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(specificationIds.SetEquals(contractIds));
    }

    private static HashSet<string> ReadProjectReferences(string projectName)
    {
        return ReadReferences(projectName, "ProjectReference")
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private static HashSet<string> ReadPackageReferences(string projectName)
    {
        return ReadReferences(projectName, "PackageReference")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> ReadReferences(string projectName, string elementName)
    {
        string repositoryRoot = RepositoryRootLocator.Find();
        string projectPath = Path.Combine(repositoryRoot, "src", projectName, $"{projectName}.csproj");
        var document = XDocument.Load(projectPath, LoadOptions.None);
        return document
            .Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .OfType<string>()
            .Where(include => !string.IsNullOrWhiteSpace(include));
    }

    private static HashSet<string> NewSet(params string[] values)
    {
        return values.ToHashSet(StringComparer.Ordinal);
    }
}
