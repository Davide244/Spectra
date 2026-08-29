namespace SpectraEngine.Core.Scene;

/// <summary>
/// What <see cref="SceneManager.LoadStartupScene"/> builds when the engine
/// comes up: the authored demo, or a near-empty baseplate for an editor
/// session.
/// </summary>
/// <remarks>
/// <b>The demo is the default, and the editor shell overrides it.</b> The demo
/// scene is the engine's end-to-end smoke test and stays what a bare
/// <c>Engine.Run</c> shows; an editor session over a real project must not
/// boot into somebody else's level, so the shell asks for
/// <see cref="Baseplate"/> and then opens the project's startup map through
/// the ordinary map path, where load failures carry a real report instead of
/// a log line.
/// </remarks>
public enum StartupSceneKind
{
    /// <summary>The authored demo scene, the engine's own smoke fixture.</summary>
    Demo,

    /// <summary>
    /// A sun and a ground plate — enough that a fresh scene is lit, has a
    /// floor to stand things on, and play mode has somewhere to walk. The
    /// deliberate echo of the platform this engine is aimed at, where every
    /// new place starts as exactly this.
    /// </summary>
    Baseplate,
}
