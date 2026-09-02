namespace SpectraEngine.Entities.Tests;

/// <summary>
/// The fixture classes the generator tests compile.
/// </summary>
/// <remarks>
/// <b>Inline rather than on disk, because each one IS the statement of its
/// test.</b> Every diagnostic fixture exists to be wrong in exactly one way, and
/// a reader has to be able to see the wrongness beside the assertion; a file
/// reference would put a six-line class in another window. The representative
/// fixture is here for the same reason: it is the input the committed snapshot
/// describes.
/// </remarks>
internal static class Fixtures
{
    /// <summary>
    /// The representative class: one keyvalue of every binding shape, two inputs
    /// and two outputs.
    /// </summary>
    /// <remarks>
    /// It carries an inferred bool, an explicitly typed <c>Color</c> and
    /// <c>AssetSound</c> (the two shapes inference deliberately refuses), a float
    /// with both bounds and a widget, and a <c>Guid</c>, which is the one type
    /// whose empty wire form the binder handles specially.
    /// </remarks>
    public const string RepresentativeEntity = """
        using SpectraEngine.Core.Entities;
        using System;
        using System.Numerics;

        namespace TestGame.Entities;

        [SpectraEntity("func_door", Display = "Door", Group = "Brush", Placement = EntityPlacement.Brush)]
        public sealed partial class FuncDoor : Entity
        {
            [EntityOutput]
            public const string OnOpened = nameof(OnOpened);

            [EntityOutput]
            public const string OnClosed = nameof(OnClosed);

            [Keyvalue("startopen", Display = "Start open", Default = "0")]
            public bool StartOpen { get; set; }

            [Keyvalue("speed", Display = "Speed", Tooltip = "Units a second.", Default = "100",
                Min = 1f, Max = 1000f, Widget = KeyvalueWidget.Slider)]
            public float Speed { get; set; } = 100f;

            [Keyvalue("glow", Display = "Glow", Default = "1 0.5 0.25", Type = KeyvalueType.Color)]
            public Vector3 Glow { get; set; }

            [Keyvalue("opensound", Display = "Open sound", Type = KeyvalueType.AssetSound)]
            public string OpenSound { get; set; } = "";

            [Keyvalue("linked", Display = "Linked node", Type = KeyvalueType.NodeRef)]
            public Guid Linked { get; set; }

            [EntityInput("Open")]
            private void Open(ref EntityInputContext context) => FireOnOpened(context.Activator);

            [EntityInput("Close")]
            private void Close(ref EntityInputContext context) => FireOnClosed(context.Activator);
        }
        """;

    /// <summary>Carries the attribute without being partial.</summary>
    public const string NotPartial = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed class LogicThing : Entity
        {
        }
        """;

    /// <summary>Two classes claiming one wire name.</summary>
    public const string DuplicateClassName = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class FirstThing : Entity
        {
        }

        [SpectraEntity("logic_thing")]
        public sealed partial class SecondThing : Entity
        {
        }
        """;

    /// <summary>A keyvalue on a type nothing is inferred from, with no stated type.</summary>
    public const string UnsupportedKeyvalueType = """
        using SpectraEngine.Core.Entities;
        using System.Collections.Generic;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class LogicThing : Entity
        {
            [Keyvalue("names")]
            public List<string> Names { get; set; } = new();
        }
        """;

    /// <summary>A stated type the member cannot carry.</summary>
    public const string KeyvalueTypeMismatch = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class LogicThing : Entity
        {
            [Keyvalue("tint", Type = KeyvalueType.Color)]
            public float Tint { get; set; }
        }
        """;

    /// <summary>A keyvalue the binder has nowhere to write.</summary>
    public const string KeyvalueNotAssignable = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class LogicThing : Entity
        {
            [Keyvalue("speed")]
            public float Speed => 100f;
        }
        """;

    /// <summary>An input the dispatch switch could not call.</summary>
    public const string InvalidInputSignature = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class LogicThing : Entity
        {
            [EntityInput("Trigger")]
            private bool Trigger(string parameter) => true;
        }
        """;

    /// <summary>A keyvalue claiming the name the node's own identity already has.</summary>
    public const string ReservedKeyvalueName = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_thing")]
        public sealed partial class LogicThing : Entity
        {
            [Keyvalue("targetname")]
            public string Who { get; set; } = "";
        }
        """;

    /// <summary>A small, correct entity, for the caching oracle's watched file.</summary>
    public const string CachedEntity = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_cached", Group = "Logic")]
        public sealed partial class LogicCached : Entity
        {
            [EntityOutput]
            public const string OnFired = nameof(OnFired);

            [Keyvalue("count", Display = "Count", Default = "0")]
            public int Count { get; set; }

            [EntityInput("Fire")]
            private void Fire(ref EntityInputContext context) => FireOnFired(context.Activator);
        }
        """;

    /// <summary>
    /// <see cref="CachedEntity"/> plus a member the generator does not read,
    /// appended after everything so no span the model records moves.
    /// </summary>
    /// <remarks>
    /// <b>An edit INSIDE the class is what forces the transform to run again.</b>
    /// Roslyn reuses the green node for a class declaration that did not change,
    /// so appending a comment to the end of the FILE leaves the attribute
    /// provider's entry cached and the transform never runs; that proves the
    /// provider is scoped and says nothing about whether the model compares by
    /// value. A new member does change the class node, so the transform runs,
    /// builds a fresh model, and only value equality can then keep the emitter
    /// from re-running.
    /// </remarks>
    public const string CachedEntityWithSpareMember = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_cached", Group = "Logic")]
        public sealed partial class LogicCached : Entity
        {
            [EntityOutput]
            public const string OnFired = nameof(OnFired);

            [Keyvalue("count", Display = "Count", Default = "0")]
            public int Count { get; set; }

            [EntityInput("Fire")]
            private void Fire(ref EntityInputContext context) => FireOnFired(context.Activator);

            private int Spare() => 1;
        }
        """;

    /// <summary>
    /// <see cref="CachedEntity"/> with a real change to what it declares, which
    /// is the caching oracle's control.
    /// </summary>
    public const string CachedEntityWithExtraKeyvalue = """
        using SpectraEngine.Core.Entities;

        namespace TestGame.Entities;

        [SpectraEntity("logic_cached", Group = "Logic")]
        public sealed partial class LogicCached : Entity
        {
            [EntityOutput]
            public const string OnFired = nameof(OnFired);

            [Keyvalue("count", Display = "Count", Default = "0")]
            public int Count { get; set; }

            [EntityInput("Fire")]
            private void Fire(ref EntityInputContext context) => FireOnFired(context.Activator);

            [Keyvalue("label", Display = "Label")]
            public string Label { get; set; } = "";
        }
        """;

    /// <summary>A file carrying no entity at all, which the caching oracle edits.</summary>
    public const string UnrelatedFile = """
        namespace TestGame.Support;

        internal static class Unrelated
        {
            public static int Value => 1;
        }
        """;

    /// <summary>The same file after an edit that touches no entity.</summary>
    public const string UnrelatedFileEdited = """
        namespace TestGame.Support;

        internal static class Unrelated
        {
            public static int Value => 2;

            public static int Other => 3;
        }
        """;
}
