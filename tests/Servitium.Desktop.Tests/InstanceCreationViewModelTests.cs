using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Java;
using Servitium.Contracts.Nodes;
using Servitium.Contracts.Operations;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceCreationViewModelTests
{
    [Theory]
    [InlineData("Succeeded", true)]
    [InlineData("Failed", false)]
    public async Task CreationWaitsForTheActualOperationBeforeClosingTheWizard(string status, bool submitted)
    {
        using CreationHandler handler = new(status);
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceCreationViewModel model = Create(client);
        model.NextCommand.Execute(null);
        var nodeId = Guid.NewGuid();
        model.Name = "Synthetic instance"; model.SelectedNode = new NodeDto(nodeId, "Synthetic node", "Online", null, null);
        model.Launch.SetRuntimes([Java(nodeId)]);
        model.NextCommand.Execute(null);
        bool closed = false; model.Submitted += (_, _) => closed = true;
        await model.CreateCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(submitted, closed);
        Assert.True(handler.ReadOperation);
        if (!submitted) { Assert.Contains("恢复", model.Message, StringComparison.Ordinal); Assert.True(model.CreateCommand.CanExecute(null)); }
    }
    [Fact]
    public void WizardCannotAdvanceWithForeignJavaOrInvalidMemoryAndPreservesArgumentBoundaries()
    {
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"));
        using InstanceCreationViewModel model = Create(client);
        Assert.True(model.NextCommand.CanExecute(null));
        model.NextCommand.Execute(null);
        Assert.False(model.NextCommand.CanExecute(null));
        var nodeId = Guid.NewGuid();
        model.Name = "合成实例";
        model.SelectedNode = new NodeDto(nodeId, "合成节点", "Online", null, null);
        JavaRuntimeDto registered = Java(nodeId);
        model.Launch.SetRuntimes([registered]);
        model.Launch.SelectedJava = Java(Guid.NewGuid());
        Assert.False(model.NextCommand.CanExecute(null));
        model.Launch.SelectedJava = registered;
        Assert.True(model.NextCommand.CanExecute(null));
        model.Launch.MaximumHeap = model.Launch.MinimumHeap - 1;
        Assert.False(model.NextCommand.CanExecute(null));
        model.Launch.MaximumHeap = double.NaN;
        Assert.False(model.NextCommand.CanExecute(null));
        model.Launch.MaximumHeap = 2048;
        model.Launch.Target = @"D:\NotRelative\server.jar";
        Assert.False(model.NextCommand.CanExecute(null));
        model.Launch.Target = "server.jar";
        model.StopTimeout = 601;
        Assert.False(model.NextCommand.CanExecute(null));
        model.StopTimeout = 60;
        model.NextCommand.Execute(null);
        Assert.True(model.CreateCommand.CanExecute(null));
        Assert.False(model.NextCommand.CanExecute(null));
        Assert.Contains("3.", model.StepTitle, StringComparison.Ordinal);
        Assert.Equal(["-Dname=two words", "-XX:+UseG1GC"], LaunchEditor.Arguments("-Dname=two words\n-XX:+UseG1GC"));
        model.SelectedNode = new NodeDto(Guid.NewGuid(), "其他节点", "Online", null, null);
        Assert.Null(model.Launch.SelectedJava);
        Assert.False(model.CreateCommand.CanExecute(null));
    }

    [Fact]
    public void AttachingRequiresAnAbsoluteDirectoryAndArchiveImportRequiresAnExplicitFile()
    {
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"));
        using InstanceCreationViewModel model = Create(client);
        model.SourceMode = 2;
        model.AbsolutePath = "relative-folder";
        Assert.False(model.NextCommand.CanExecute(null));
        model.AbsolutePath = @"D:\Isolated\Instance";
        Assert.True(model.NextCommand.CanExecute(null));
        model.SourceMode = 1;
        Assert.False(model.NextCommand.CanExecute(null));
        model.ArchivePath = @"D:\Isolated\fixture.zip";
        Assert.True(model.NextCommand.CanExecute(null));
    }

    private static InstanceCreationViewModel Create(ServitiumApiClient client)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans"); return new(localization, client);
    }
    private static JavaRuntimeDto Java(Guid nodeId) { return new(Guid.NewGuid(), nodeId, @"D:\Java\bin\java.exe", "25", "Synthetic", "x64", "registered", "Java 25", 25, false, DateTimeOffset.UtcNow, null, 0); }
    private sealed partial class CreationHandler(string status) : HttpMessageHandler
    {
        private readonly Guid _operationId = Guid.NewGuid();
        internal bool ReadOperation { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted) { Content = JsonContent.Create(new OperationAcceptedDto(_operationId, "CreateInstance", "Queued")) });
            }
            ReadOperation = true;
            OperationDetailsDto result = new(new(_operationId, "CreateInstance", status, 1, null, status == "Failed" ? "node-offline" : null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch), "Synthetic", null, null, false);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result) });
        }
    }
}
