using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace SpectraEngine.Core.Entities;

/// <summary>
/// What entity classes this build knows: class name to a factory that builds one
/// and the <see cref="EntitySchema"/> that describes it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Registration is static, enumeration is SORTED, and the two facts are
/// connected.</b> The intended producer is a generated <c>[ModuleInitializer]</c>
/// per entity class, and the order module initializers run in is decided by the
/// loader: it is stable enough to look deterministic in a debug run and is not a
/// guarantee. The binary schema artifact this feeds must be byte-stable across
/// runs, so every enumeration here is ordered by class name with
/// <see cref="string.CompareOrdinal"/> and registration order is never
/// observable.
/// </para>
/// <para>
/// <b>Frozen on first read.</b> A class registered after something has already
/// resolved a name would change what a map means halfway through a load, and a
/// schema artifact exported before it would be missing an entry that a later run
/// happens to include. The freeze makes that a throw at the registration rather
/// than a difference somebody notices in a file.
/// </para>
/// <para>
/// <b>An instance type with a <see cref="Shared"/> singleton, not a static
/// class.</b> The process-wide registry is what generated code registers into,
/// but a catalogue that can only ever be the process-wide one cannot be tested
/// (the freeze makes test order load-bearing) and cannot be scoped to one game.
/// One type, two ways to reach it.
/// </para>
/// <para>
/// <b>No reflection anywhere.</b> A factory is a delegate the generator emits, so
/// nothing here needs a type name, an <c>Activator.CreateInstance</c> or an
/// assembly scan, all three of which a trimmed AOT build removes.
/// </para>
/// </remarks>
public sealed class EntityCatalog
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byClassName = new(StringComparer.Ordinal);

    // Non-null exactly when the catalogue is frozen, and written inside the lock
    // after the dictionary is complete: a reader that sees this array has seen
    // every entry that will ever be in the dictionary, so reads past the freeze
    // need no lock at all.
    private volatile EntitySchema[]? _sorted;

    /// <summary>The process-wide catalogue that generated registrations feed.</summary>
    public static EntityCatalog Shared { get; } = new();

    /// <summary>Registers <paramref name="schema"/>'s class into <see cref="Shared"/>.</summary>
    public static void Register(EntitySchema schema, Func<Entity> factory) => Shared.Add(schema, factory);

    /// <summary>Whether this catalogue has been read and can no longer be added to.</summary>
    public bool IsFrozen => _sorted is not null;

    /// <summary>
    /// Every registered class's schema, ordered by class name. Reading this
    /// freezes the catalogue.
    /// </summary>
    public IReadOnlyList<EntitySchema> Schemas => Freeze();

    /// <summary>Registers one entity class.</summary>
    /// <exception cref="InvalidOperationException">
    /// The class name is already registered, or the catalogue has been read.
    /// </exception>
    public void Add(EntitySchema schema, Func<Entity> factory)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(factory);

        lock (_gate)
        {
            if (_sorted is not null)
            {
                throw new InvalidOperationException(
                    $"The entity catalogue was already read, so '{schema.ClassName}' cannot be registered. " +
                    "Every class must register before the first lookup.");
            }

            if (_byClassName.ContainsKey(schema.ClassName))
            {
                throw new InvalidOperationException(
                    $"An entity class named '{schema.ClassName}' is already registered. " +
                    "Two classes claiming one name is a map that means different things in different builds.");
            }

            _byClassName.Add(schema.ClassName, new Entry(schema, factory));
        }
    }

    /// <summary>
    /// Builds one instance of <paramref name="className"/>, or returns false when
    /// nothing is registered under that name. Freezes the catalogue.
    /// </summary>
    public bool TryCreate(string? className, [NotNullWhen(true)] out Entity? entity)
    {
        Freeze();

        if (className is not null && _byClassName.TryGetValue(className, out Entry entry))
        {
            entity = entry.Factory();
            return entity is not null;
        }

        entity = null;
        return false;
    }

    /// <summary>
    /// The schema registered for <paramref name="className"/>. Freezes the
    /// catalogue.
    /// </summary>
    public bool TryGetSchema(string? className, [NotNullWhen(true)] out EntitySchema? schema)
    {
        Freeze();

        if (className is not null && _byClassName.TryGetValue(className, out Entry entry))
        {
            schema = entry.Schema;
            return true;
        }

        schema = null;
        return false;
    }

    private EntitySchema[] Freeze()
    {
        EntitySchema[]? sorted = _sorted;
        if (sorted is not null)
            return sorted;

        lock (_gate)
        {
            if (_sorted is not null)
                return _sorted;

            var ordered = new EntitySchema[_byClassName.Count];
            int i = 0;
            foreach (Entry entry in _byClassName.Values)
                ordered[i++] = entry.Schema;

            // Ordinal, and never the current culture: a catalogue sorted by a
            // machine's locale would export a different schema file on a
            // different machine from the same source.
            Array.Sort(ordered, static (a, b) => string.CompareOrdinal(a.ClassName, b.ClassName));
            _sorted = ordered;
            return ordered;
        }
    }

    private readonly record struct Entry(EntitySchema Schema, Func<Entity> Factory);
}
