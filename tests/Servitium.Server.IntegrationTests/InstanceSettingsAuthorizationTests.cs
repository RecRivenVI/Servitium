using System;
using System.Collections.Generic;

using Servitium.Contracts.Instances;
using Servitium.Server.Endpoints;

using Xunit;

namespace Servitium.Server.IntegrationTests;

public sealed class InstanceSettingsAuthorizationTests
{
    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-342")]
    public void 普通用户只修改名称和标签时不触发危险设置判定()
    {
        UpdateInstanceRequest current = CreateRequest();
        UpdateInstanceRequest requested = current with
        {
            Name = "Renamed",
            Tags = ["survival", "public"],
            Launch = current.Launch with
            {
                EnvironmentVariables = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["SECOND"] = "2",
                    ["FIRST"] = "1",
                },
            },
        };

        Assert.False(InstanceEndpoints.HasDangerousInstanceChange(current, requested));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("TestId", "SEC-343")]
    public void 普通用户修改任一危险实例设置时被识别()
    {
        UpdateInstanceRequest current = CreateRequest();

        Assert.True(InstanceEndpoints.HasDangerousInstanceChange(current, current with { Notes = "changed" }));
        Assert.True(InstanceEndpoints.HasDangerousInstanceChange(current, current with
        {
            Launch = current.Launch with { JvmArguments = ["-XX:+UseZGC"] },
        }));
        Assert.True(InstanceEndpoints.HasDangerousInstanceChange(current, current with
        {
            WorkingDirectory = @"D:\Servers\other",
        }));
        Assert.True(InstanceEndpoints.HasDangerousInstanceChange(current, current with
        {
            Rcon = current.Rcon with { Enabled = true },
        }));
        Assert.True(InstanceEndpoints.HasDangerousInstanceChange(current, current with
        {
            ShortcutCommands = [new("stop", "stop")],
        }));
    }

    private static UpdateInstanceRequest CreateRequest()
    {
        return new(
            "Server",
            ["survival"],
            "notes",
            new(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                1024,
                4096,
                ["-XX:+UseG1GC"],
                "jar",
                "server.jar",
                ["nogui"],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["FIRST"] = "1",
                    ["SECOND"] = "2",
                }),
            "stop",
            30,
            false,
            true,
            3,
            new(false, 25575, null),
            new(true, "127.0.0.1", 25565),
            @"D:\Servers\server",
            25565,
            0,
            5,
            600,
            null,
            new(),
            new(),
            [new("save", "save-all")]);
    }
}
