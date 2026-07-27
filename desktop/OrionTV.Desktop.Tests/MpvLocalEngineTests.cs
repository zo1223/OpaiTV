using OrionTV.Desktop.App.LocalPlayback;
using System.Text.Json;

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
        Assert.Contains("--mute=no", arguments);
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

    [Fact]
    public void BuildLoadCommand_binds_resume_position_to_the_file_load()
    {
        var command = MpvLocalEngine.BuildLoadCommand(@"E:\Videos\episode.mkv", 970_565);

        Assert.Equal("loadfile", command[0]);
        Assert.Equal(@"E:\Videos\episode.mkv", command[1]);
        Assert.Equal("replace", command[2]);
        Assert.Equal(-1, command[3]);
        var options = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(command[4]);
        Assert.Equal("970.565", options["start"]);
    }

    [Fact]
    public void BuildLoadCommand_does_not_add_a_start_option_for_a_fresh_play()
    {
        var command = MpvLocalEngine.BuildLoadCommand(@"E:\Videos\episode.mkv", 0);

        Assert.Equal(["loadfile", @"E:\Videos\episode.mkv", "replace"], command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TryReadBooleanResponse_reads_matching_successful_response(bool expected)
    {
        using var document = JsonDocument.Parse(
            $$"""{"request_id":42,"error":"success","data":{{expected.ToString().ToLowerInvariant()}}}""");

        var matched = MpvLocalEngine.TryReadBooleanResponse(document.RootElement, 42, out var actual);

        Assert.True(matched);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("{\"request_id\":41,\"error\":\"success\",\"data\":true}")]
    [InlineData("{\"request_id\":42,\"error\":\"property unavailable\"}")]
    [InlineData("{\"request_id\":42,\"error\":\"success\",\"data\":null}")]
    public void TryReadBooleanResponse_rejects_unrelated_or_invalid_response(string json)
    {
        using var document = JsonDocument.Parse(json);

        var matched = MpvLocalEngine.TryReadBooleanResponse(document.RootElement, 42, out _);

        Assert.False(matched);
    }
}
