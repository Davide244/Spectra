using System;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// One keyvalue, reduced to values: what to assign, what to call it on the wire,
/// and how to read it.
/// </summary>
/// <param name="MemberName">The C# member the parsed value is assigned to.</param>
/// <param name="Name">The wire name, as a map file spells it.</param>
/// <param name="Display">The editor label, already derived if the author stated none.</param>
/// <param name="Tooltip">One sentence of help, or empty.</param>
/// <param name="Default">The default value, as text.</param>
/// <param name="Type">The <c>KeyvalueType</c>'s frozen wire byte.</param>
/// <param name="Widget">A <c>KeyvalueWidget</c> value.</param>
/// <param name="Min">Lower bound, or NaN.</param>
/// <param name="Max">Upper bound, or NaN.</param>
internal sealed record KeyvalueModel(
    string MemberName,
    string Name,
    string Display,
    string Tooltip,
    string Default,
    byte Type,
    byte Widget,
    float Min,
    float Max) : IEquatable<KeyvalueModel>;

/// <summary>One input: the wire name, and the method the dispatch switch calls.</summary>
/// <param name="Name">The wire name, as a map file spells it.</param>
/// <param name="MethodName">The C# method that receives it.</param>
internal sealed record InputModel(string Name, string MethodName) : IEquatable<InputModel>;

/// <summary>
/// Everything the emitter needs about one attributed class, and NOTHING that
/// would keep a compilation alive.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the whole caching contract.</b> An <see cref="Microsoft.CodeAnalysis.ISymbol"/> or a
/// <see cref="Microsoft.CodeAnalysis.SyntaxNode"/> reaching this record would pin the compilation it
/// came from and compare by reference, so every run would report every model as
/// changed. Everything downstream would still produce the RIGHT source - which
/// is exactly why it is not caught by any test that only looks at output - and
/// the cost would show up months later as an IDE that has become slow to type
/// in. Every member here is a string, a primitive or an
/// <see cref="EquatableArray{T}"/> of those.
/// </para>
/// <para>
/// <b>The diagnostics ride along</b> because they are decided where the symbols
/// are and reported where the source is written; see <see cref="DiagnosticInfo"/>.
/// </para>
/// </remarks>
/// <param name="Namespace">The containing namespace, or empty for the global one.</param>
/// <param name="ContainingTypes">
/// The types this class is nested in, outermost first, each already spelled as
/// the partial declaration the emitter reopens it with.
/// </param>
/// <param name="TypeName">The C# type name.</param>
/// <param name="FullTypeName">The type's fully qualified name, for hint names and messages.</param>
/// <param name="ClassName">The wire name, as a map file spells it.</param>
/// <param name="Display">The editor label, already derived if the author stated none.</param>
/// <param name="Group">The category an editor files it under, or empty.</param>
/// <param name="Placement">An <c>EntityPlacement</c> member name.</param>
/// <param name="IsPartial">Whether this class and every type containing it are partial.</param>
/// <param name="Keyvalues">The keyvalues, in declaration order.</param>
/// <param name="Inputs">The inputs, in declaration order.</param>
/// <param name="Outputs">The output names, in declaration order.</param>
/// <param name="Diagnostics">What the transform decided was wrong with the declaration.</param>
/// <param name="Location">Where the class is, for the diagnostics reported about it as a whole.</param>
internal sealed record EntityModel(
    string Namespace,
    EquatableArray<string> ContainingTypes,
    string TypeName,
    string FullTypeName,
    string ClassName,
    string Display,
    string Group,
    string Placement,
    bool IsPartial,
    EquatableArray<KeyvalueModel> Keyvalues,
    EquatableArray<InputModel> Inputs,
    EquatableArray<string> Outputs,
    EquatableArray<DiagnosticInfo> Diagnostics,
    LocationInfo? Location) : IEquatable<EntityModel>;
