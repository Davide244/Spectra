using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// The entity classes something knows ABOUT, parsed from a <c>.sentdef</c>
/// image: schemas and nothing else, with no factory and no way to build an
/// instance.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="LoadFromSentDef"/> is the only way to populate one, and that is
/// structural rather than stylistic.</b> The editor's property panel and its
/// wiring UI are consumers of <see cref="EntitySchema"/> and of no other input,
/// which is what makes parity between a generated C# class and a Luau
/// definition a property of the design instead of something to keep checking.
/// A second constructor taking a schema list would be exactly the hole that
/// invariant closes: the in-process editor would quietly read the catalogue
/// directly, the out-of-process one would read the file, and the two would
/// drift with nothing failing. Going through the bytes also means the round trip
/// is exercised on every launch rather than only in a test.
/// </para>
/// <para>
/// <b>Not <see cref="EntityCatalog"/>, and deliberately a different type.</b>
/// That one maps a class name to a FACTORY and lives in the process that can
/// build the class; this one carries description only, so it is what a tool
/// holds for a game whose assembly it has never loaded. Reading a schema out of
/// it can never make something runnable, which is the honest shape of what a
/// file can promise.
/// </para>
/// <para>
/// <b>Immutable once loaded, and therefore free to share.</b> Nothing here takes
/// a lock, because there is no mutation to guard: the arrays are built inside
/// the load and handed out read-only afterwards.
/// </para>
/// </remarks>
public sealed class EntitySchemaCatalog
{
    private readonly EntitySchema[] _schemas;
    private readonly Dictionary<string, EntitySchema> _byClassName;

    // Private, and it stays private: see the remarks above for why the only
    // entry point is the one that parses bytes.
    private EntitySchemaCatalog(EntitySchema[] schemas)
    {
        _schemas = schemas;

        // Ordinal, matching EntityCatalog: a case-folding or culture-aware
        // lookup would resolve a class name on one machine and not on another,
        // for the same map.
        _byClassName = new Dictionary<string, EntitySchema>(schemas.Length, StringComparer.Ordinal);
        foreach (EntitySchema schema in schemas)
            _byClassName.Add(schema.ClassName, schema);
    }

    /// <summary>
    /// Parses a <c>.sentdef</c> image.
    /// </summary>
    /// <remarks>
    /// A span rather than a stream or a path, because a mounted pack hands out
    /// spans into a memory-mapped view and this is the one shape that costs no
    /// copy there. Nothing is retained past the call.
    /// </remarks>
    /// <param name="image">The complete file.</param>
    /// <exception cref="SentDefFormatException">The image is not one this build can read.</exception>
    public static EntitySchemaCatalog LoadFromSentDef(ReadOnlySpan<byte> image) =>
        new(SentDef.Read(image));

    /// <summary>
    /// Every class the image declared, in its order: sorted by class name with
    /// <see cref="string.CompareOrdinal"/>, which the reader enforces.
    /// </summary>
    public IReadOnlyList<EntitySchema> Schemas => _schemas;

    /// <summary>How many classes this catalogue describes.</summary>
    public int Count => _schemas.Length;

    /// <summary>
    /// The schema declared for <paramref name="className"/>, or false when this
    /// catalogue has never heard of it.
    /// </summary>
    /// <remarks>
    /// <b>A miss is an ordinary answer, never an error.</b> A level may name a
    /// class authored against a game whose definitions are not mounted, and that
    /// level still loads, round-trips and saves: what the entity loses is its
    /// schema-driven property editor, not its authored data.
    /// </remarks>
    public bool TryGetSchema(string? className, [NotNullWhen(true)] out EntitySchema? schema)
    {
        if (className is not null)
            return _byClassName.TryGetValue(className, out schema);

        schema = null;
        return false;
    }
}
