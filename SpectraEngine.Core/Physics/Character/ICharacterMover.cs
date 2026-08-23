namespace SpectraEngine.Core.Physics.Character;

/// <summary>
/// One fixed tick of character movement: the algorithm, as a replaceable thing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This interface exists so that replacing the mover is not forking the
/// engine.</b> The intent for this engine is that a game developer writes their
/// own character controller in script and installs it, the way a Roblox
/// developer replaces the default one. Until something accepted a substitute,
/// that was impossible by construction: the simulation called a static method
/// directly, so the only way to change movement was to edit engine C#.
/// </para>
/// <para>
/// <b>The signature is deliberately the shape a script can implement.</b>
/// Everything crossing it is a value or an interface: the state is a plain
/// struct, the command is a plain struct, and the world is reached only through
/// <see cref="ICharacterCollisionSource"/>. There is no camera, no input device,
/// no renderer and no clock, so an implementation cannot depend on anything a
/// dedicated server or a rollback replay would not have.
/// </para>
/// <para>
/// <b>A substitute is not required to match <see cref="CharacterMover"/>.</b>
/// The built-in one is a default and a starting point, not a specification. A
/// replacement is free to feel completely different, and no parity test asserts
/// otherwise.
/// </para>
/// </remarks>
public interface ICharacterMover
{
    /// <summary>Advances <paramref name="state"/> by exactly one fixed tick.</summary>
    /// <remarks>
    /// Must be a pure function of its arguments. An implementation that reads a
    /// clock, samples input, writes the scene or keeps state between calls
    /// breaks replay, which is what network reconciliation is built on.
    /// </remarks>
    void Tick(
        ref CharacterState state,
        in CharacterCommand command,
        ICharacterCollisionSource source,
        CharacterTuning tuning,
        float deltaTime);
}

/// <summary>
/// The engine's built-in mover, as an <see cref="ICharacterMover"/>.
/// </summary>
/// <remarks>
/// A thin adapter over the static <see cref="CharacterMover"/> rather than a
/// rewrite of it. Keeping the algorithm static keeps it obviously stateless,
/// which is the property the whole seam rests on; the singleton exists only
/// because an interface needs an instance.
/// </remarks>
public sealed class DefaultCharacterMover : ICharacterMover
{
    /// <summary>The shared instance. It holds no state, so one serves every character.</summary>
    public static readonly DefaultCharacterMover Instance = new();

    private DefaultCharacterMover()
    {
    }

    /// <inheritdoc/>
    public void Tick(
        ref CharacterState state,
        in CharacterCommand command,
        ICharacterCollisionSource source,
        CharacterTuning tuning,
        float deltaTime)
        => CharacterMover.Tick(ref state, in command, source, tuning, deltaTime);
}
