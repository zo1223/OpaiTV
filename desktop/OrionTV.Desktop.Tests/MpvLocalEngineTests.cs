using OrionTV.Desktop.App.LocalPlayback;
using System.Text.Json;
using System.Windows.Threading;

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

    [Fact]
    public void BuildSeekCommand_uses_exact_absolute_seek_for_keyboard_and_slider()
    {
        var command = MpvLocalEngine.BuildSeekCommand(970_565);

        Assert.Equal("seek", command[0]);
        Assert.Equal("970.565", command[1]);
        Assert.Equal("absolute+exact", command[2]);
    }

    [Theory]
    [InlineData(230_000, 225_000, 230_050, false)]
    [InlineData(230_000, 225_000, 228_500, false)]
    [InlineData(230_000, 225_000, 225_250, true)]
    [InlineData(230_000, 215_000, 218_500, true)]
    [InlineData(230_000, 200_000, 210_000, true)]
    [InlineData(230_000, 235_000, 230_050, false)]
    [InlineData(230_000, 235_000, 234_750, true)]
    [InlineData(230_000, 245_000, 241_500, true)]
    [InlineData(230_000, 260_000, 250_000, true)]
    [InlineData(230_000, 230_000, 230_000, true)]
    public void SeekObservationFilter_rejects_pre_seek_position_until_target_is_reported(
        long originMs,
        long targetMs,
        long observedMs,
        bool expected)
    {
        Assert.Equal(
            expected,
            MpvLocalEngine.HasReachedSeekTarget(originMs, targetMs, observedMs));
    }

    [Fact]
    public void SeekBurstOrigin_survives_intermediate_confirmation_and_resets_on_reversal()
    {
        const int generation = 7;
        const long deadline = 2_000;

        Assert.Equal(
            (230_000L, 240_000L),
            MpvLocalEngine.ResolveRelativeSeekBurst(
                existingOriginMs: 230_000,
                existingTargetMs: 235_000,
                existingGeneration: generation,
                existingDeadline: deadline,
                currentMs: 235_000,
                deltaMs: 5_000,
                generation: generation,
                now: 1_000));

        Assert.Equal(
            (240_000L, 235_000L),
            MpvLocalEngine.ResolveRelativeSeekBurst(
                existingOriginMs: 230_000,
                existingTargetMs: 240_000,
                existingGeneration: generation,
                existingDeadline: deadline,
                currentMs: 240_000,
                deltaMs: -5_000,
                generation: generation,
                now: 1_001));

        Assert.Equal(
            (235_000L, 240_000L),
            MpvLocalEngine.ResolveRelativeSeekBurst(
                existingOriginMs: 230_000,
                existingTargetMs: 235_000,
                existingGeneration: generation,
                existingDeadline: deadline,
                currentMs: 235_000,
                deltaMs: 5_000,
                generation: generation,
                now: deadline));

        Assert.Equal(
            (235_000L, 240_000L),
            MpvLocalEngine.ResolveRelativeSeekBurst(
                existingOriginMs: 230_000,
                existingTargetMs: 235_000,
                existingGeneration: generation,
                existingDeadline: deadline,
                currentMs: 235_000,
                deltaMs: 5_000,
                generation: generation + 1,
                now: 1_000));
    }

    [Fact]
    public void RelativeSeekBurst_keeps_requested_targets_stable_across_ts_corrections()
    {
        using var engine = new MpvLocalEngine(new IntPtr(1), Dispatcher.CurrentDispatcher);

        Assert.True(engine.TrySeekBy(5_000));
        Assert.Equal(5_000, engine.Time);
        Assert.True(engine.TryApplyObservedTime(11_000));

        Assert.True(engine.TrySeekBy(5_000));
        Assert.Equal(10_000, engine.Time);
        Assert.True(engine.TryApplyObservedTime(16_000));

        Assert.True(engine.TrySeekBy(5_000));
        Assert.Equal(15_000, engine.Time);
        Assert.True(engine.TryApplyObservedTime(21_000));

        Assert.True(engine.TrySeekBy(-5_000));
        Assert.Equal(16_000, engine.Time);
    }

    [Fact]
    public void AbsoluteSeek_ends_relative_burst_before_next_keyboard_seek()
    {
        using var engine = new MpvLocalEngine(new IntPtr(1), Dispatcher.CurrentDispatcher);

        Assert.True(engine.TrySeekBy(5_000));
        Assert.True(engine.TryApplyObservedTime(11_000));

        Assert.True(engine.TrySeek(30_000));
        Assert.True(engine.TryApplyObservedTime(36_000));

        Assert.True(engine.TrySeekBy(5_000));
        Assert.Equal(41_000, engine.Time);
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
