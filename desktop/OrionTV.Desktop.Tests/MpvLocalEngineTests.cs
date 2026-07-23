using OrionTV.Desktop.App.LocalPlayback;

namespace OrionTV.Desktop.Tests;

public sealed class MpvLocalEngineTests
{
    [Fact]
    public void BuildArguments_uses_local_playback_stability_defaults()
    {
        var arguments = MpvLocalEngine.BuildArguments(1234, @"\\.\pipe\orion-test");

        Assert.Contains("--cache=no", arguments);
        Assert.Contains("--hr-seek=yes", arguments);
        Assert.Contains("--fullscreen=no", arguments);
        Assert.DoesNotContain(arguments, argument => argument.StartsWith("--demuxer-max-bytes=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, argument => argument == "--hr-seek=no");
        Assert.Contains($"--slang={MpvLocalEngine.PreferredSubtitleLanguages}", arguments);
        Assert.Contains($"--sub-font={MpvLocalEngine.PreferredSubtitleFont}", arguments);
        Assert.Contains("--sub-font-size=42", arguments);
        Assert.Contains("--osc=no", arguments);
        Assert.Contains("--input-default-bindings=no", arguments);
        Assert.DoesNotContain("--osc=yes", arguments);
    }

    [Fact]
    public void BuildArguments_keeps_ipc_endpoint_and_window_handle_as_single_arguments()
    {
        var pipe = @"\\.\pipe\orion test endpoint";
        var arguments = MpvLocalEngine.BuildArguments(987654321, pipe);

        Assert.Contains("--wid=987654321", arguments);
        Assert.Contains($"--input-ipc-server={pipe}", arguments);
        Assert.DoesNotContain(arguments, argument => argument.Contains(' ')
            && !argument.StartsWith("--sub-font=", StringComparison.Ordinal)
            && !argument.StartsWith("--input-ipc-server=", StringComparison.Ordinal));
    }
}
