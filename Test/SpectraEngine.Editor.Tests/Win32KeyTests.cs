using SpectraEngine.Core.Input;
using SpectraEngine.Editor.Viewport.Windows;

namespace SpectraEngine.Editor.Tests;

/// <summary>
/// Virtual keys to the engine's vocabulary, which is the shell's whole keyboard.
/// </summary>
/// <remarks>
/// <b>A wrong entry here is silent.</b> The shortcut simply stops working, or
/// worse fires a different tool, and nothing logs. The letter and digit rows are
/// range arithmetic rather than fifty hand-written cases precisely so that they
/// cannot be mistyped one at a time, and this checks the arithmetic lands where
/// the enum's own ordering says it should.
/// </remarks>
public sealed class Win32KeyTests
{
    // Virtual-key codes are ASCII for the letter and digit rows.
    private const int VkA = 0x41;
    private const int VkW = 0x57;
    private const int VkZ = 0x5A;
    private const int Vk0 = 0x30;
    private const int Vk3 = 0x33;
    private const int Vk9 = 0x39;

    // Bit 24 of lParam is the extended-key flag, which is what separates the
    // right-hand Control and Alt from the left.
    private const nint Plain = 0;
    private const nint Extended = 1 << 24;

    [Theory]
    [InlineData(VkA, InputKey.A)]
    [InlineData(VkW, InputKey.W)]
    [InlineData(VkZ, InputKey.Z)]
    public void The_letter_row_maps_by_range(int virtualKey, InputKey expected) =>
        Win32Keys.ToInputKey(virtualKey, Plain).ShouldBe(expected);

    [Theory]
    [InlineData(Vk0, InputKey.Number0)]
    [InlineData(Vk3, InputKey.Number3)]
    [InlineData(Vk9, InputKey.Number9)]
    public void The_digit_row_maps_by_range(int virtualKey, InputKey expected) =>
        Win32Keys.ToInputKey(virtualKey, Plain).ShouldBe(expected);

    [Fact]
    public void Control_and_alt_are_separated_by_the_extended_bit()
    {
        // The engine binds the LEFT ones specifically (left Control descends the
        // fly camera, left Shift boosts it), so collapsing the two sides would
        // make the right-hand keys either dead or wrong.
        Win32Keys.ToInputKey(0x11, Plain).ShouldBe(InputKey.ControlLeft);
        Win32Keys.ToInputKey(0x11, Extended).ShouldBe(InputKey.ControlRight);
        Win32Keys.ToInputKey(0x12, Plain).ShouldBe(InputKey.AltLeft);
        Win32Keys.ToInputKey(0x12, Extended).ShouldBe(InputKey.AltRight);
    }

    [Theory]
    [InlineData(0x70, InputKey.F1)]
    [InlineData(0x7B, InputKey.F12)]
    [InlineData(0x1B, InputKey.Escape)]
    [InlineData(0x2E, InputKey.Delete)]
    [InlineData(0x20, InputKey.Space)]
    [InlineData(0x26, InputKey.Up)]
    public void Named_keys_map_to_their_own_names(int virtualKey, InputKey expected) =>
        Win32Keys.ToInputKey(virtualKey, Plain).ShouldBe(expected);

    [Theory]
    [InlineData(0xDB, InputKey.LeftBracket)]
    [InlineData(0xDD, InputKey.RightBracket)]
    public void The_snap_ladder_keys_map(int virtualKey, InputKey expected) =>
        // [ and ] step the snap increment, and they are OEM codes rather than
        // characters, so they are the easiest pair in the table to get wrong.
        Win32Keys.ToInputKey(virtualKey, Plain).ShouldBe(expected);

    [Fact]
    public void A_key_the_engine_does_not_name_is_unknown_rather_than_nearby()
    {
        // Keypad 7 mapping to the number row would fire whatever tool 7 binds.
        Win32Keys.ToInputKey(0x67, Plain).ShouldBe(InputKey.Unknown);
        Win32Keys.ToInputKey(0x87, Plain).ShouldBe(InputKey.Unknown);
    }
}
