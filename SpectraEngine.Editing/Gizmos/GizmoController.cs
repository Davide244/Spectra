using SpectraEngine.Core.Graphics;
using SpectraEngine.Core.Scene;
using SpectraEngine.Editing.Input;
using SpectraEngine.Editing.Undo;
using System;

namespace SpectraEngine.Editing.Gizmos;

/// <summary>
/// The viewport's manipulator: owns the move, rotate and resize tools, keeps
/// exactly one of them live, and routes the per-frame update, the drawing, and
/// the mode/orientation/snap verbs to it.
/// </summary>
/// <remarks>
/// <b>Switching modes abandons any gesture in progress.</b> A tool left
/// mid-drag would hold an open undo transaction, and the next tool's
/// <c>BeginTransaction</c> would throw because transactions do not nest — so the
/// switch calls <see cref="GizmoTool.Reset"/>, which cancels the drag and
/// restores the selection exactly. That is also what a user means: the drag
/// never finished.
/// <para>
/// <b>The three tools share their orientation and their snap policy, not their
/// increments.</b> Flipping to local affects move and rotate together (resize is
/// local-only by construction), and toggling snap affects all three — one
/// setting the user learns once. Each tool keeps its own increment, because a
/// grid step, an angle step and a factor step are different quantities.
/// </para>
/// <para>
/// <b>No keyboard vocabulary lives here.</b> The host resolves a keypress into a
/// <see cref="GizmoCommand"/> — <see cref="GizmoShortcuts"/> carries the
/// recommended default bindings — and calls <see cref="Apply"/>. That is what
/// keeps this assembly free of the windowing backend; see
/// <see cref="EditorInputFrame"/> for the same seam on the pointer side.
/// </para>
/// <para>
/// <b>Threading:</b> render thread only, like the tools and the scene.
/// </para>
/// </remarks>
public sealed class GizmoController
{
    private GizmoMode _mode = GizmoMode.Translate;
    private GizmoOrientation _orientation = GizmoOrientation.World;
    private GizmoStyle _style = GizmoStyle.Studio;

    /// <summary>
    /// Creates a manipulator over a scene and the history its edits land in.
    /// </summary>
    /// <param name="scene">The scene whose selection the tools manipulate.</param>
    /// <param name="undo">
    /// The history every tool opens its transactions in. Must be the history for
    /// <paramref name="scene"/>.
    /// </param>
    public GizmoController(Scene scene, UndoStack undo)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ArgumentNullException.ThrowIfNull(undo);

        Scene = scene;
        Undo = undo;
        Translate = new TranslateGizmo(scene, undo);
        Rotate = new RotateGizmo(scene, undo);
        Scale = new ScaleGizmo(scene, undo);

        // Explicit rather than relying on the tools' own default, so the
        // controller's idea of the live style and the tools' cannot start out
        // disagreeing if either default ever moves.
        Translate.Style = _style;
        Rotate.Style = _style;
        Scale.Style = _style;
    }

    /// <summary>The scene the tools manipulate.</summary>
    public Scene Scene { get; }

    /// <summary>The history every tool's drags land in.</summary>
    public UndoStack Undo { get; }

    /// <summary>The move tool.</summary>
    public TranslateGizmo Translate { get; }

    /// <summary>The rotate tool.</summary>
    public RotateGizmo Rotate { get; }

    /// <summary>The resize tool.</summary>
    public ScaleGizmo Scale { get; }

    /// <summary>
    /// Which tool is live. Assigning a different mode resets the outgoing tool,
    /// abandoning any drag it was in the middle of.
    /// </summary>
    public GizmoMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            // Reset BEFORE the switch, so the tool that owns the open
            // transaction is the one that cancels it.
            Active.Reset();
            _mode = value;
            ModeChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Whether the handles lie along the world axes or the reference node's own.
    /// Applies to <see cref="Translate"/> and <see cref="Rotate"/>;
    /// <see cref="Scale"/> is local-only and ignores it.
    /// </summary>
    /// <remarks>
    /// Changing it mid-drag would swing the constraint out from under the cursor,
    /// so — like a mode switch — it resets the live tool first.
    /// </remarks>
    public GizmoOrientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value)
                return;

            Active.Reset();
            _orientation = value;
            Translate.Orientation = value;
            Rotate.Orientation = value;
            OrientationChanged?.Invoke(value);
        }
    }

    /// <summary>
    /// Which manipulator style all three tools wear: the handle roster, where
    /// the handles stand, where the pivot sits, and what a resize holds still.
    /// Defaults to <see cref="GizmoStyle.Studio"/>.
    /// </summary>
    /// <remarks>
    /// <b>It fans out to all three tools, unlike <see cref="Orientation"/>.</b>
    /// Orientation skips the resize tool because that tool opts out of the whole
    /// question (<see cref="GizmoTool.SupportsOrientation"/>); style is not
    /// optional for anybody, and a viewport whose move tool was Studio's and
    /// whose resize tool was Blender's would be the exact mismatch the styles
    /// exist to end.
    /// <para>
    /// Changing it mid-drag would move the handles out from under a live grab, so
    /// like a mode switch it resets the live tool first.
    /// </para>
    /// </remarks>
    public GizmoStyle Style
    {
        get => _style;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (ReferenceEquals(_style, value))
                return;

            Active.Reset();
            _style = value;
            Translate.Style = value;
            Rotate.Style = value;
            Scale.Style = value;
            StyleChanged?.Invoke(value);
        }
    }

    /// <summary>The live tool.</summary>
    public GizmoTool Active => _mode switch
    {
        GizmoMode.Rotate => Rotate,
        GizmoMode.Scale => Scale,
        _ => Translate,
    };

    /// <summary>Raised after <see cref="Mode"/> actually changed.</summary>
    public event Action<GizmoMode>? ModeChanged;

    /// <summary>Raised after <see cref="Orientation"/> actually changed.</summary>
    public event Action<GizmoOrientation>? OrientationChanged;

    /// <summary>Raised after <see cref="Style"/> actually changed.</summary>
    public event Action<GizmoStyle>? StyleChanged;

    /// <summary>
    /// Whether snapping is on. Reading reports the live tool's setting; writing
    /// sets all three, so the user's snap toggle means one thing across the
    /// editor.
    /// </summary>
    public bool SnapEnabled
    {
        get => _mode switch
        {
            GizmoMode.Rotate => Rotate.Snap.Enabled,
            GizmoMode.Scale => Scale.Snap.Enabled,
            _ => Translate.Snap.Enabled,
        };
        set
        {
            Translate.Snap.Enabled = value;
            Rotate.Snap.Enabled = value;
            Scale.Snap.Enabled = value;
        }
    }

    /// <summary>Advances the live tool by one frame. See <see cref="GizmoTool.Update"/>.</summary>
    public GizmoUpdateResult Update(in EditorInputFrame frame, bool cancelRequested = false, bool pointerAvailable = true) =>
        Active.Update(in frame, cancelRequested, pointerAvailable);

    /// <summary>Draws the live tool. See <see cref="GizmoTool.Draw"/>.</summary>
    public void Draw(DebugDraw output) => Active.Draw(output);

    /// <summary>
    /// Applies one editor verb and reports whether it changed anything —
    /// so a host can fall through to another binding when it did not.
    /// </summary>
    public bool Apply(GizmoCommand command)
    {
        switch (command)
        {
            case GizmoCommand.UseTranslate:
                return SetMode(GizmoMode.Translate);

            case GizmoCommand.UseRotate:
                return SetMode(GizmoMode.Rotate);

            case GizmoCommand.UseScale:
                return SetMode(GizmoMode.Scale);

            case GizmoCommand.CycleMode:
                // Translate → Rotate → Scale → Translate: the order the toolbar
                // shows them in, so a repeated press walks left to right.
                return SetMode(_mode switch
                {
                    GizmoMode.Translate => GizmoMode.Rotate,
                    GizmoMode.Rotate => GizmoMode.Scale,
                    _ => GizmoMode.Translate,
                });

            case GizmoCommand.ToggleOrientation:
                Orientation = _orientation == GizmoOrientation.World
                    ? GizmoOrientation.Local
                    : GizmoOrientation.World;
                return true;

            case GizmoCommand.ToggleStyle:
                Style = ReferenceEquals(_style, GizmoStyle.Studio)
                    ? GizmoStyle.Classic
                    : GizmoStyle.Studio;
                return true;

            case GizmoCommand.ToggleSnap:
                SnapEnabled = !SnapEnabled;
                return true;

            case GizmoCommand.UseWorldOrientation:
                return SetOrientation(GizmoOrientation.World);

            case GizmoCommand.UseLocalOrientation:
                return SetOrientation(GizmoOrientation.Local);

            case GizmoCommand.UseStudioStyle:
                return SetStyle(GizmoStyle.Studio);

            case GizmoCommand.UseClassicStyle:
                return SetStyle(GizmoStyle.Classic);

            case GizmoCommand.EnableSnap:
                return SetSnapEnabled(true);

            case GizmoCommand.DisableSnap:
                return SetSnapEnabled(false);

            case GizmoCommand.FinerSnap:
                CycleSnap(-1);
                return true;

            case GizmoCommand.CoarserSnap:
                CycleSnap(1);
                return true;

            case GizmoCommand.Cancel:
                return Active.CancelDrag();

            default:
                return false;
        }
    }

    /// <summary>
    /// Resets every tool — cancelling any drag and restoring the selection. For
    /// a host that lost focus, reloaded the scene, or is tearing the viewport
    /// down.
    /// </summary>
    public void Reset()
    {
        Translate.Reset();
        Rotate.Reset();
        Scale.Reset();
    }

    private bool SetMode(GizmoMode mode)
    {
        if (_mode == mode)
            return false;

        Mode = mode;
        return true;
    }

    // The idempotent halves report whether anything changed, like SetMode, so
    // a host can fall through to another binding on a no-op.

    private bool SetOrientation(GizmoOrientation orientation)
    {
        if (_orientation == orientation)
            return false;

        Orientation = orientation;
        return true;
    }

    private bool SetStyle(GizmoStyle style)
    {
        if (ReferenceEquals(_style, style))
            return false;

        Style = style;
        return true;
    }

    private bool SetSnapEnabled(bool enabled)
    {
        if (SnapEnabled == enabled)
            return false;

        SnapEnabled = enabled;
        return true;
    }

    /// <summary>
    /// Sets one tool's snap increment directly — the binding behind a
    /// command-surface field, where the ladder keys step and this types.
    /// </summary>
    /// <remarks>
    /// Per tool rather than "the live tool's", because a UI shows the move
    /// grid and the rotate angle side by side and each field must edit its own
    /// unit. The value is validated by <see cref="SnapSettings.Increment"/>
    /// (positive only); callers refuse bad input before arriving here, the
    /// same rule the property panel follows.
    /// </remarks>
    public void SetSnapIncrement(GizmoMode tool, float increment)
    {
        SnapSettings snap = tool switch
        {
            GizmoMode.Rotate => Rotate.Snap,
            GizmoMode.Scale => Scale.Snap,
            _ => Translate.Snap,
        };
        snap.Increment = increment;
    }

    // All three ladders move together: the user is expressing "finer" or
    // "coarser", not "finer, but only for whichever tool happens to be live".
    private void CycleSnap(int direction)
    {
        Translate.Snap.CyclePreset(direction);
        Rotate.Snap.CyclePreset(direction);
        Scale.Snap.CyclePreset(direction);
    }
}
