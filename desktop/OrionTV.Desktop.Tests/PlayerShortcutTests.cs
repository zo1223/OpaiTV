using System.Windows.Input;
using OrionTV.Desktop.App.Views;

namespace OrionTV.Desktop.Tests;

public sealed class PlayerShortcutTests
{
    [Theory]
    [InlineData(Key.Space, "TogglePlayPause")]
    [InlineData(Key.Left, "SeekBackward")]
    [InlineData(Key.Right, "SeekForward")]
    [InlineData(Key.Up, "VolumeUp")]
    [InlineData(Key.Down, "VolumeDown")]
    [InlineData(Key.F, "ToggleFullscreen")]
    [InlineData(Key.M, "ToggleMute")]
    [InlineData(Key.Escape, "Escape")]
    public void ShortcutMap_resolves_player_keys(Key key, string expected)
    {
        Assert.True(PlayerShortcutMap.TryResolve(key, ModifierKeys.None, out var command));
        Assert.Equal(expected, command.ToString());
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.Enter)]
    [InlineData(Key.Tab)]
    public void ShortcutMap_ignores_unrelated_keys(Key key)
    {
        Assert.False(PlayerShortcutMap.TryResolve(key, ModifierKeys.None, out _));
    }

    [Theory]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Alt)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Windows)]
    public void ShortcutMap_does_not_override_modified_keys(ModifierKeys modifiers)
    {
        Assert.False(PlayerShortcutMap.TryResolve(Key.F, modifiers, out _));
    }
}
