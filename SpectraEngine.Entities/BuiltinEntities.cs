using SpectraEngine.Core.Entities;
using System;
using System.Collections.Generic;

namespace SpectraEngine.Entities;

/// <summary>
/// The anchor a host calls to make sure this assembly's entity classes are in
/// the catalogue.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration happens in a generated <c>[ModuleInitializer]</c> per class,
/// and a module initializer only runs once its module is LOADED.</b> Nothing in a
/// game statically calls into this assembly - a level names <c>logic_relay</c> as
/// text, and the catalogue resolves it at run time - so a trimmed or NativeAOT
/// publish has every reason to drop the whole thing, and the symptom is not a
/// missing assembly error: every map still loads, every entity of these classes
/// becomes a <c>PlaceholderEntity</c>, and the level does nothing while reporting
/// one warning per class. This is exactly the shape of
/// <c>SilkPlatform.EnsureRegistered</c>, and it is here for exactly the same
/// reason.
/// </para>
/// <para>
/// <b><see cref="Schemas"/> is what does the anchoring, and the check is what
/// makes reading it observable.</b> A static field initializer that touches one
/// member of each class is what keeps the trimmer from removing types nothing
/// else names; a discarded read of it would be free for the JIT to elide, taking
/// the class initializer with it, so the count is compared and a mismatch throws.
/// </para>
/// <para>
/// <b>Idempotent and safe to call from any host</b>, like the platform anchor:
/// every call after the first observes the same array. Call it before the first
/// <see cref="EntityWorld.Activate"/>, because reading the catalogue freezes it.
/// </para>
/// </remarks>
public static class BuiltinEntities
{
    /// <summary>How many entity classes this assembly declares.</summary>
    /// <remarks>
    /// Stated as a constant rather than read from <see cref="Schemas"/> so that a
    /// class dropped from the list is a throw naming the count, instead of an
    /// anchor that silently stopped anchoring one of them.
    /// </remarks>
    public const int ClassCount = 3;

    /// <summary>Every built-in class's schema, in declaration order.</summary>
    public static IReadOnlyList<EntitySchema> Schemas { get; } =
    [
        LogicRelay.SpectraSchema,
        LogicTimer.SpectraSchema,
        MathCounter.SpectraSchema,
    ];

    /// <summary>
    /// Loads this assembly, which is what runs the generated registrations.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// A class went missing from <see cref="Schemas"/>, so it would never have
    /// been registered either.
    /// </exception>
    public static void EnsureRegistered()
    {
        if (Schemas.Count != ClassCount)
        {
            throw new InvalidOperationException(
                $"The built-in entity anchor lists {Schemas.Count} classes and expects {ClassCount}. " +
                "A class missing from the anchor is a class a trimmed build may drop, which makes every " +
                "map naming it load as a placeholder that behaves as nothing.");
        }
    }
}
