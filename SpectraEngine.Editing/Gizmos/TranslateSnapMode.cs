namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// What a snapped translate drag quantises.
/// </summary>
/// <remarks>
/// Both Roblox Studio and Blender default to snapping the DISPLACEMENT: a part
/// grabbed at x = 0.3 and dragged one 1-unit notch lands at 1.3, its sub-grid
/// offset preserved. An earlier revision of this engine snapped the absolute
/// destination in world orientation and documented it as "what Studio does",
/// which was wrong on the reference behavior and, for a multi-selection,
/// anchored the rounding on the invisible pivot average so NO node landed on
/// the grid. Absolute snapping is still genuinely useful (it is Hammer's model
/// and Blender's opt-in "Absolute Grid Snap"), so it stays as the opt-in mode,
/// anchored where the fix put it: on the reference node, the one the user is
/// looking at.
/// </remarks>
public enum TranslateSnapMode
{
    /// <summary>
    /// Quantise the drag's displacement; the selection's sub-grid offsets
    /// survive. The default, matching Studio and Blender.
    /// </summary>
    Delta,

    /// <summary>
    /// Quantise the reference node's absolute destination onto the world grid;
    /// the other nodes keep their offsets relative to it. Hammer's model, and
    /// Blender's "Absolute Grid Snap". World orientation only — a local frame
    /// has no absolute grid, so local drags always snap the displacement.
    /// </summary>
    AbsoluteGrid,
}
