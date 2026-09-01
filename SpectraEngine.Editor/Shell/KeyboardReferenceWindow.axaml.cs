using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>One line of the keyboard reference.</summary>
/// <param name="Keys">The chord, written the way a user would say it.</param>
/// <param name="What">What it does, in the effect's words rather than the mechanism's.</param>
public sealed record KeyboardRow(string Keys, string What);

/// <summary>One headed group of the keyboard reference.</summary>
public sealed record KeyboardSection(string Name, IReadOnlyList<KeyboardRow> Rows);

/// <summary>
/// The shell's keyboard reference: every chord the editor answers to, grouped.
/// </summary>
/// <remarks>
/// <para>
/// <b>A real top-level window, which is what lets it cross the viewport.</b>
/// The airspace rule constrains content Avalonia draws INSIDE the main window;
/// a separate window is composited by the OS above the viewport's native child
/// like any other, so this may be as large as it needs to be.
/// </para>
/// <para>
/// <b>The table is authored, not generated.</b> There is no single source to
/// generate it from: the chords live in the main window's
/// <c>KeyBindings</c>, in the viewport's <c>ShellChord</c> interception and in
/// the engine's own keymap, and a reference built from any one of those would
/// silently claim the other two do not exist. Authoring it means it can be
/// wrong; generating it from a third of the truth means it is wrong by
/// construction.
/// </para>
/// </remarks>
public partial class KeyboardReferenceWindow : Window
{
    public KeyboardReferenceWindow()
    {
        InitializeComponent();
        DataContext = this;
        Opened += (_, _) => DarkCaption.Apply(this);
    }

    /// <summary>The reference, in reading order.</summary>
    public IReadOnlyList<KeyboardSection> Sections { get; } =
    [
        new("Getting around", [
            new("Right-drag", "Look. Hold it and use W A S D, Q and E to fly."),
            new("Alt + drag", "Orbit the selection."),
            new("Middle-drag", "Pan."),
            new("Wheel", "Zoom toward the cursor."),
            new("F", "Frame the selection."),
            new("F7", "Switch between the editor camera and the engine fly camera."),
        ]),

        new("Selecting", [
            new("Click", "Select what is under the cursor."),
            new("Ctrl + click", "Add to or remove from the selection."),
            new("Drag on empty space", "Box select."),
            new("Right-click", "Open the scene menu on whatever is under the cursor."),
            new("Esc", "Clear the selection."),
        ]),

        new("Building", [
            new("Ctrl + 1", "Insert a block: solid geometry that merges with the level."),
            new("Ctrl + 2", "Insert a part: moves freely, never merges."),
            new("Ctrl + 3", "Insert a cut: carves a hole out of the blocks it overlaps."),
            new("Ctrl + 4", "Insert a light."),
            new("Ctrl + D", "Duplicate the selection."),
            new("Del", "Delete the selection."),
            new("Ctrl + G", "Group the selection."),
            new("Ctrl + Shift + G", "Ungroup."),
            new("Ctrl + T", "Convert the selection between block and part."),
            new("F2", "Rename, with the scene tree focused."),
        ]),

        new("Moving things", [
            new("2  or  W", "Move tool."),
            new("3  or  R", "Size tool."),
            new("4  or  E", "Rotate tool."),
            new("X", "Swap the drag axes between world and local."),
            new("Y", "Swap the handles between Studio and Classic."),
            new("Shift (while sizing)", "Ask for the other anchoring for one drag."),
            new("G", "Snap to the grid."),
            new("[  and  ]", "Finer and coarser grid."),
            new("Alt (while dragging)", "Invert snapping for that one drag."),
            new("Esc (while dragging)", "Cancel the drag and put it back."),
        ]),

        new("History and files", [
            new("Ctrl + Z", "Undo."),
            new("Ctrl + Y", "Redo. Ctrl + Shift + Z does the same."),
            new("Ctrl + S", "Save the level."),
            new("Ctrl + Shift + S", "Save as."),
            new("Ctrl + N", "New level."),
            new("Ctrl + O", "Open a level."),
        ]),

        new("Looking at the world", [
            new("F8", "Walk the level in first person. F8 or Esc leaves."),
            new("F9", "Draw the character capsule, while playing."),
            new("F1 - F5", "Wireframe, CSG vertices, bounds, face normals, node axes."),
            new("F6", "Cycle the rendering pipeline."),
        ]),
    ];

    private void OnCloseClicked(object? sender, RoutedEventArgs e) => Close();
}
