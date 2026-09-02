using System;
using System.Collections;
using System.Collections.Generic;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// An immutable array that compares by VALUE, for use inside an incremental
/// generator's model.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because <c>ImmutableArray&lt;T&gt;</c> does not compare by
/// value.</b> Its <c>Equals</c> is reference equality over the underlying array,
/// so a model holding one is unequal to a freshly built copy of itself: every
/// run reports the model as changed, every downstream step re-runs, and the
/// generator's caching is dead. Nothing fails, nothing warns, and the only
/// symptom is an IDE that gets slower as a solution grows.
/// </para>
/// <para>
/// <b>Ordered comparison, deliberately.</b> Keyvalue and input order is
/// declaration order, which is what a property panel lays out and what an
/// exported schema writes, so two models differing only in member order really
/// are different models.
/// </para>
/// </remarks>
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    public EquatableArray(T[]? items) => _items = items;

    /// <summary>An array with no entries.</summary>
    public static EquatableArray<T> Empty => new(Array.Empty<T>());

    public int Count => _items is null ? 0 : _items.Length;

    public T this[int index] => _items![index];

    public bool Equals(EquatableArray<T> other)
    {
        T[]? left = _items;
        T[]? right = other._items;

        if (ReferenceEquals(left, right))
            return true;
        if (left is null || right is null || left.Length != right.Length)
            return false;

        for (int i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
                return false;
        }

        return true;
    }

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        // Hand-rolled: System.HashCode is not in netstandard2.0, and a hash that
        // ignored the contents would be legal and would turn every dictionary
        // Roslyn keys on this into a linear scan.
        unchecked
        {
            int hash = 17;
            if (_items is not null)
            {
                for (int i = 0; i < _items.Length; i++)
                    hash = (hash * 31) + _items[i].GetHashCode();
            }

            return hash;
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        T[] items = _items ?? Array.Empty<T>();
        for (int i = 0; i < items.Length; i++)
            yield return items[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Builds an <see cref="EquatableArray{T}"/> from the shape the transform collects into.</summary>
internal static class EquatableArray
{
    public static EquatableArray<T> From<T>(List<T> items)
        where T : IEquatable<T> => new(items.ToArray());
}
