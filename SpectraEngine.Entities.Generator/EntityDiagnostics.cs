using Microsoft.CodeAnalysis;

namespace SpectraEngine.Entities.Generator;

/// <summary>
/// Every diagnostic this generator can report.
/// </summary>
/// <remarks>
/// <para>
/// <b>All of them are errors, and that is the design.</b> Each one describes a
/// declaration the generator cannot turn into working code, and the alternative
/// to failing the build is emitting a class that compiles and behaves as
/// nothing: a keyvalue that is never bound, an input a map wires and no entity
/// answers. Both are silent, and both are found by a level designer rather than
/// by the person who wrote the class.
/// </para>
/// <para>
/// <b>The ids are frozen.</b> They travel in build logs and suppression files,
/// so a renumber invalidates somebody's <c>NoWarn</c> and their build changes
/// meaning without anything being edited. New diagnostics take the next free
/// number.
/// </para>
/// </remarks>
internal static class EntityDiagnostics
{
    private const string Category = "SpectraEntities";

    /// <summary>SPE001: the class carries the attribute and is not partial.</summary>
    public static readonly DiagnosticDescriptor NotPartial = new(
        id: "SPE001",
        title: "Entity class must be partial",
        messageFormat:
            "Entity class '{0}' must be declared partial (and so must every type containing it), because the " +
            "generator emits the other half of it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE002: two classes in one compilation claim the same wire name.</summary>
    public static readonly DiagnosticDescriptor DuplicateClassName = new(
        id: "SPE002",
        title: "Duplicate entity class name",
        messageFormat:
            "Entity class name '{0}' is declared by both '{1}' and '{2}'. Two classes claiming one name is a map " +
            "that means different things in different builds, and the catalogue refuses the second at start-up.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE003: no <c>KeyvalueType</c> can be inferred from the member's own type.</summary>
    public static readonly DiagnosticDescriptor UnsupportedKeyvalueType = new(
        id: "SPE003",
        title: "Unsupported keyvalue member type",
        messageFormat:
            "Keyvalue '{0}' is declared on a member of type '{1}', which no KeyvalueType is inferred from. State " +
            "the type explicitly (Type = KeyvalueType.Color, say), or use one of: bool, int, uint, float, string, " +
            "Vector2, Vector3, Vector4, Guid.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE004: an input method is not shaped the way the dispatch switch calls one.</summary>
    public static readonly DiagnosticDescriptor InvalidInputSignature = new(
        id: "SPE004",
        title: "Entity input has the wrong signature",
        messageFormat:
            "Entity input method '{0}' must be a non-generic instance method shaped " +
            "'void {0}(ref EntityInputContext context)'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE005: a keyvalue claims the name that IS the node's identity.</summary>
    public static readonly DiagnosticDescriptor ReservedKeyvalueName = new(
        id: "SPE005",
        title: "Reserved keyvalue name",
        messageFormat:
            "Keyvalue '{0}' on '{1}' is reserved: targetname IS SceneNode.Name, so a keyvalue of that name forks " +
            "the identity into two fields that a rename updates one of",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE006: the declared <c>KeyvalueType</c> cannot be stored in the member.</summary>
    public static readonly DiagnosticDescriptor KeyvalueTypeMismatch = new(
        id: "SPE006",
        title: "Keyvalue type does not match the member",
        messageFormat:
            "Keyvalue '{0}' declares KeyvalueType.{1}, which is read as '{2}', but the member's type is '{3}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    /// <summary>SPE007: the generated binder has nothing it can assign to.</summary>
    public static readonly DiagnosticDescriptor KeyvalueNotAssignable = new(
        id: "SPE007",
        title: "Keyvalue member cannot be assigned",
        messageFormat:
            "Keyvalue '{0}' must be a settable instance field or property; '{1}' is not one, so the generated " +
            "binder would have nowhere to put the parsed value",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
