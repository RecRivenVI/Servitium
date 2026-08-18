using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Agent.Hosting;

using Xunit;

namespace Servitium.Agent.IntegrationTests;

public sealed class AgentHostTests
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("TestId", "INT-002")]
    public async Task Agent_控制台自检模式_输出Pid并正常退出()
    {
        string dataRoot = Path.Combine(Path.GetTempPath(), $"servitium-agent-test-{Guid.NewGuid():N}");
        try
        {
            string agentAssembly = typeof(AgentWorker).Assembly.Location;
            using Process process = new();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            process.StartInfo.ArgumentList.Add(agentAssembly);
            process.StartInfo.ArgumentList.Add("--self-test");
            process.StartInfo.Environment["SERVITIUM_DATA_ROOT"] = dataRoot;

            Assert.True(process.Start());
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(true);
            string standardOutput = await standardOutputTask.ConfigureAwait(true);
            string standardError = await standardErrorTask.ConfigureAwait(true);

            Assert.True(process.ExitCode == 0, standardError);
            Assert.Contains("Servitium.Agent PID=", standardOutput, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(dataRoot, "pid")));
        }
        finally
        {
            if (Directory.Exists(dataRoot))
            {
                Directory.Delete(dataRoot, recursive: true);
            }
        }
    }

}
