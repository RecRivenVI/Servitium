using System;
using System.Threading;
using System.Threading.Tasks;

using Google.Protobuf;

using Servitium.Agent.ControlPlane;
using Servitium.Contracts.Agent.V1;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentCommandDispatcherTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1490")]
    public async Task 指令分派_所有带载荷操作一致拒绝损坏Json()
    {
        AgentCommandDispatcher dispatcher = CreatePayloadOnlyDispatcher();
        foreach (CommandType type in Enum.GetValues<CommandType>())
        {
            if (type is CommandType.Unspecified or CommandType.ListJava or CommandType.DiscoverJava or CommandType.Reconcile)
            {
                continue;
            }

            CommandResponse response = await dispatcher.DispatchAsync(new CommandRequest
            {
                CommandId = "malformed-" + type,
                Type = type,
                Payload = ByteString.CopyFromUtf8("{"),
            }, CancellationToken.None).ConfigureAwait(true);

            Assert.False(response.Succeeded);
            Assert.False(response.Accepted);
            Assert.Equal("validation-failed", response.Error.Code);
            Assert.Equal("malformed-" + type, response.CommandId);
        }
    }

    [Theory]
    [InlineData(CommandType.Unspecified)]
    [InlineData((CommandType)999)]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-1491")]
    public async Task 指令分派_拒绝未指定和未知操作(CommandType type)
    {
        CommandResponse response = await CreatePayloadOnlyDispatcher().DispatchAsync(new CommandRequest
        {
            CommandId = "unsupported",
            Type = type,
        }, CancellationToken.None).ConfigureAwait(true);

        Assert.False(response.Succeeded);
        Assert.Equal("validation-failed", response.Error.Code);
    }

    private static AgentCommandDispatcher CreatePayloadOnlyDispatcher()
    {
        // 该组测试只进入每个分支的 JSON 边界；损坏载荷必须在触碰任何运行服务前被拒绝。
        return new(null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
    }
}
