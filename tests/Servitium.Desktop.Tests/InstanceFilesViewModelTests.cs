using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using Servitium.Client;
using Servitium.Contracts.Files;
using Servitium.Contracts.Instances;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed partial class InstanceFilesViewModelTests
{
    [Fact]
    public async Task SelectionEnablesOnlyApplicableCommandsAndCancelledDeleteSendsNoMutation()
    {
        using FileHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceFilesViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        Assert.False(model.DeleteCommand.CanExecute(null));
        model.Select([model.Files[0]]);
        Assert.True(model.DeleteCommand.CanExecute(null));
        Assert.True(model.EditTextCommand.CanExecute(null));
        Assert.False(model.PreviewCommand.CanExecute(null));
        await model.DeleteCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(0, handler.Mutations);
        model.CopyCommand.Execute(null);
        Assert.True(model.PasteCommand.CanExecute(null));
        model.Destination = "outside.zip";
        model.Url = "file:///D:/private";
        Assert.False(model.DownloadUrlCommand.CanExecute(null));
        model.Url = "https://example.invalid/synthetic";
        Assert.True(model.DownloadUrlCommand.CanExecute(null));
        model.Destination = @"..\outside.zip";
        Assert.False(model.DownloadUrlCommand.CanExecute(null));
        Assert.False(model.CompressCommand.CanExecute(null));
    }

    [Fact]
    public async Task DirtyEditorBlocksDirectoryChangesAndKeepsExplicitEncoding()
    {
        using FileHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceFilesViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        model.Select([model.Files[0]]);
        model.TextEncoding = "UTF-8-BOM";
        await model.EditTextCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal("UTF-8-BOM", model.TextEncoding);
        model.EditorText = "changed content";
        Assert.True(model.IsDirty);
        Assert.True(model.SaveTextCommand.CanExecute(null));
        int reads = handler.Reads;
        await model.NavigateAsync("logs").ConfigureAwait(true);
        Assert.Equal(string.Empty, model.DirectoryPath);
        Assert.True(model.IsDirty);
        Assert.Equal(reads, handler.Reads);
    }

    [Fact]
    public async Task LoadingMoreKeepsEarlierRowsAndDirectoryTabsRespectTheLastTab()
    {
        using FileHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceFilesViewModel model = Create(client);
        await model.LoadAsync().ConfigureAwait(true);
        _ = Assert.Single(model.Files);
        Assert.True(model.HasMore);
        await model.LoadMoreCommand.ExecuteAsync(null).ConfigureAwait(true);
        Assert.Equal(2, model.Files.Count);
        Assert.False(model.HasMore);
        Assert.False(model.CanCloseTab);
        model.NewTabCommand.Execute(null);
        Assert.Equal(2, model.Tabs.Count);
        Assert.True(model.ShowTabs);
        await model.CloseTabCommand.ExecuteAsync(null).ConfigureAwait(true);
        _ = Assert.Single(model.Tabs);
        Assert.False(model.CanCloseTab);
        Assert.False(model.ShowTabs);
    }

    [Theory]
    [InlineData(@"..\outside")]
    [InlineData(@"D:\outside")]
    [InlineData("/outside")]
    public async Task UnsafeDirectoryPathsNeverReachTheApi(string path)
    {
        using FileHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceFilesViewModel model = Create(client);
        _ = await Assert.ThrowsAsync<ArgumentException>(() => model.NavigateAsync(path)).ConfigureAwait(true);
        Assert.Equal(0, handler.Reads);
    }

    [Theory]
    [InlineData("Running", true, false, true)]
    [InlineData("Stopped", true, true, true)]
    [InlineData("Crashed", true, true, true)]
    [InlineData("Stopped", false, false, false)]
    public async Task FileWritesRespectTheExistingAgentStateGateWithoutBlockingAllowedReads(string status, bool reachable, bool writes, bool reads)
    {
        using FileHandler handler = new();
        using ServitiumApiClient client = new(new Uri("http://127.0.0.1:58132"), handler: handler);
        using InstanceFilesViewModel model = Create(client, status, reachable);
        await model.LoadAsync().ConfigureAwait(true);
        model.Select([model.Files[0]]);
        Assert.Equal(writes, model.UploadCommand.CanExecute(null));
        Assert.Equal(writes, model.DeleteCommand.CanExecute(null));
        Assert.Equal(writes, model.CreateDirectoryCommand.CanExecute(null));
        Assert.Equal(reads, model.DownloadCommand.CanExecute(null));
        Assert.Equal(reads, model.EditTextCommand.CanExecute(null));
    }

    private static InstanceFilesViewModel Create(ServitiumApiClient client, string status = "Stopped", bool reachable = true)
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        InstanceDto instance = new(Guid.NewGuid(), Guid.NewGuid(), "合成实例", status, @"D:\Isolated\Instance", false, []);
        return new(localization, client, instance, (_, _, _) => Task.FromResult<string?>(null), (_, _) => Task.FromResult(false),
            () => Task.FromResult<string?>(null), (_, _) => Task.FromResult<string?>(null), (_, _) => Task.CompletedTask, reachable);
    }
    private sealed partial class FileHandler : HttpMessageHandler
    {
        internal int Reads { get; private set; }
        internal int Mutations { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) { Mutations++; throw new InvalidOperationException("Cancelled mutations must not be sent."); }
            Reads++;
            object response = request.RequestUri!.AbsolutePath.EndsWith("/text", StringComparison.Ordinal)
                ? new TextFileDto("server.properties", "test content", "utf-8", "lf", 12)
                : new FileEntryPageDto([new(request.RequestUri.Query.Contains("cursor=next", StringComparison.Ordinal) ? "other.txt" : "server.properties", false, 12, DateTimeOffset.UnixEpoch)],
                    request.RequestUri.Query.Contains("cursor=next", StringComparison.Ordinal) ? null : "next");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(response) });
        }
    }
}
