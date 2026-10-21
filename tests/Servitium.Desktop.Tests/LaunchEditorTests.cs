using System;
using Servitium.Contracts.Instances;
using Servitium.Contracts.Java;
using Servitium.Desktop.Services;
using Servitium.Desktop.ViewModels.Instances;

using Xunit;

namespace Servitium.Desktop.Tests;

public sealed class LaunchEditorTests
{
    [Fact]
    public void InvalidMemoryAndEscapingTargetsCannotProduceAnEnabledFormAndEmptyNumberBoxesRemainComparable()
    {
        using LaunchEditor model = Create();
        Assert.True(model.IsValid);
        model.MaximumHeap = double.NaN;
        Assert.False(model.IsValid);
        Assert.Contains("NaN", model.Snapshot(), StringComparison.Ordinal);
        model.MaximumHeap = 1048577; Assert.False(model.IsValid);
        model.MaximumHeap = 2048;
        model.Target = @"..\outside.jar"; Assert.False(model.IsValid);
        model.Target = @"D:\outside.jar"; Assert.False(model.IsValid);
        model.Target = "server.jar";
        model.JvmArguments = "-Dname=two words\n-XX:+UseG1GC";
        Assert.True(model.IsValid);
        Assert.Equal(["-Dname=two words", "-XX:+UseG1GC"], model.Request().JvmArguments);
    }

    [Fact]
    public void EnvironmentValuesArePreservedButExcludedFromPreviewAndWindowsDuplicateNamesAreRejected()
    {
        using LaunchEditor model = Create();
        model.AddEnvironmentCommand.Execute(null);
        model.EnvironmentVariables[0].Name = "SYNTHETIC_KEY";
        model.EnvironmentVariables[0].Value = "synthetic-private-value\nsecond line";
        Assert.True(model.IsValid);
        Assert.Equal("synthetic-private-value\nsecond line", model.Request().EnvironmentVariables!["SYNTHETIC_KEY"]);
        Assert.DoesNotContain("synthetic-private-value", model.Preview, StringComparison.Ordinal);
        model.AddEnvironmentCommand.Execute(null);
        model.EnvironmentVariables[1].Name = "synthetic_key";
        Assert.False(model.IsValid);
        model.RemoveEnvironmentCommand.Execute(model.EnvironmentVariables[1]);
        Assert.True(model.IsValid);
        model.EnvironmentVariables[0].Value = "invalid\0value";
        Assert.False(model.IsValid);
    }

    [Fact]
    public void AdvancedModeRequiresNodePolicyAndKeepsTheRegisteredExecutableIdentity()
    {
        using LaunchEditor model = Create();
        Guid javaId = model.SelectedJava!.Id;
        model.AdvancedMode = true; model.AdvancedArguments = "-version";
        Assert.False(model.IsValid);
        model.AdvancedAllowed = true;
        Assert.True(model.IsValid);
        StructuredLaunchRequest request = model.Request();
        Assert.Equal(javaId, request.JavaRuntimeId);
        Assert.True(request.AdvancedMode);
        Assert.Equal(["-version"], request.AdvancedArguments);
        model.AdvancedAllowed = false;
        Assert.True(model.CanChangeAdvanced);
        model.AdvancedMode = false;
        Assert.False(model.CanChangeAdvanced);
        Assert.True(model.IsValid);
    }

    [Theory]
    [InlineData("-Dname=two words", "\"-Dname=two words\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("folder with space\\", "\"folder with space\\\\\"")]
    public void CommandPreviewQuotesWindowsArgumentBoundaries(string value, string expected)
    {
        Assert.Equal(expected, LaunchEditor.QuoteArgument(value));
    }

    private static LaunchEditor Create()
    {
        LocalizationService localization = new(); localization.Reload("zh-Hans");
        var nodeId = Guid.NewGuid();
        LaunchEditor model = new(localization) { NodeId = nodeId };
        model.SetRuntimes([new JavaRuntimeDto(Guid.NewGuid(), nodeId, @"D:\Synthetic Java\bin\java.exe", "25", "Synthetic", "x64", "registered", "Java 25", 25, false, DateTimeOffset.UnixEpoch, null, 0)]);
        return model;
    }
}
