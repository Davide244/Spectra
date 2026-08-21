using SpectraEngine.Core.Input;
using SpectraEngine.Core.Scene;

namespace SpectraEngine.Editing.Selection;

/// <summary>
/// The one place the editor decides what a modifier key means for selection:
/// nothing held replaces, Shift adds, Ctrl toggles.
/// </summary>
/// <remarks>
/// It is a keymap decision, so it lives in the editing layer rather than beside
/// <see cref="SelectionUpdate"/> in the engine — but it is <em>one</em> keymap
/// decision, shared by click-select and box select, because a user who learns
/// that Shift adds should not discover that it means something else inside a
/// marquee.
/// <para>
/// Shift wins when both are held. Ctrl+Shift is a distinct gesture in some
/// editors ("add, never remove"), but treating it as plain add is the
/// least-surprising reading of an ambiguous input, and it never removes
/// something the user was pointing at.
/// </para>
/// </remarks>
public static class SelectionModifiers
{
    /// <summary>Resolves held modifiers into the selection combination they mean.</summary>
    public static SelectionUpdate Resolve(KeyModifiers modifiers)
    {
        if ((modifiers & KeyModifiers.Shift) != 0)
            return SelectionUpdate.Add;

        if ((modifiers & KeyModifiers.Control) != 0)
            return SelectionUpdate.Toggle;

        return SelectionUpdate.Replace;
    }

    /// <summary>
    /// True when the modifiers mean "keep what is already selected" — the test
    /// a click on empty space uses to decide between clearing the selection and
    /// leaving it alone.
    /// </summary>
    public static bool IsAdditive(KeyModifiers modifiers) =>
        Resolve(modifiers) != SelectionUpdate.Replace;
}
