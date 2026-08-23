using System;
using System.Numerics;

namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// A character being simulated: its state, its tuning, the world it collides
/// against, and one fixed tick.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes a scene and nothing else, and that is the whole point.</b> No
/// camera, no input manager, no renderer, no debug draw. Three things this
/// engine intends to do all require simulating a character with none of those
/// present: a dedicated server runs the world headlessly, a rollback replays a
/// tick many times per correction, and a scripted mover has to be bindable
/// without dragging rendering types into the scripting surface. A constructor
/// that demanded a camera would have made all three impossible in the same
/// stroke.
/// </para>
/// <para>
/// <b>Input arrives as a value, not as a device.</b> A
/// <see cref="CharacterCommand"/> is what a keyboard, a network packet, a replay
/// buffer and a test all produce, so the simulation cannot tell which it is
/// talking to. <c>FirstPersonController</c> is the one that knows about a
/// keyboard.
/// </para>
/// <para>
/// <b>What it deliberately does NOT own:</b> the view. Eye height, the smoothing
/// that absorbs a step, and the interpolation between two ticks are render-only
/// values, and putting them here would make a replayed tick depend on where the
/// head happened to be.
/// </para>
/// </remarks>
public sealed class CharacterSimulation
{
    private readonly ICharacterCollisionSource _source;
    private CharacterState _state;

    /// <summary>Builds a character over a scene's geometry.</summary>
    public CharacterSimulation(Scene.Scene scene, CharacterTuning? tuning = null)
    {
        ArgumentNullException.ThrowIfNull(scene);

        Tuning = tuning ?? new CharacterTuning();

        // The plane-set source, not a hull source: this is what makes a doorway
        // cut by a subtractive brush walkable, because it evaluates
        // union(additive) minus union(subtractive) per query rather than
        // approximating each brush by its uncut convex hull.
        var brushSource = new BrushPlaneCollisionSource(scene, Tuning);
        _source = brushSource;
        Collision = brushSource;

        _state = CharacterState.AtFeet(Vector3.Zero);
    }

    /// <summary>Every movement constant, live. Editing one takes effect next tick.</summary>
    public CharacterTuning Tuning { get; }

    /// <summary>The brush-plane source, for the counters it discloses.</summary>
    public BrushPlaneCollisionSource Collision { get; }

    /// <summary>Feet position, velocity and ground state: the whole of what is simulated.</summary>
    public CharacterState State => _state;

    /// <summary>Where <see cref="Spawn"/> and the fall-out guard put the character.</summary>
    public Vector3 SpawnPosition { get; set; }

    /// <summary>Below this height the character is respawned rather than left falling.</summary>
    public float FallOutHeight { get; set; } = -1000f;

    /// <summary>Times the fall-out guard has fired.</summary>
    public int Respawns { get; private set; }

    /// <summary>Horizontal speed in spectraunits per second, what a speedometer would read.</summary>
    public float HorizontalSpeed => new Vector2(_state.Velocity.X, _state.Velocity.Z).Length();

    /// <summary>Puts the character at its spawn, at rest.</summary>
    public void Spawn() => _state = CharacterState.AtFeet(SpawnPosition);

    /// <summary>
    /// Replaces the state wholesale. What a network correction and a replay both
    /// need, and the reason <see cref="CharacterState"/> is a struct.
    /// </summary>
    public void Restore(in CharacterState state) => _state = state;

    /// <summary>
    /// Advances by one fixed tick. Returns true if the fall-out guard fired,
    /// which the caller may want to report; the simulation itself does not log.
    /// </summary>
    public bool Tick(in CharacterCommand command, float deltaTime)
    {
        CharacterMover.Tick(ref _state, in command, _source, Tuning, deltaTime);

        if (_state.Position.Y >= FallOutHeight)
            return false;

        Respawns++;
        Spawn();
        return true;
    }
}
