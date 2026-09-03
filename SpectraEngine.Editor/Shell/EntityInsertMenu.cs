using SpectraEngine.Core.Entities;
using System.Collections.Generic;

namespace SpectraEngine.Editor.Shell;

/// <summary>One entry of the Insert menu's entity submenu.</summary>
/// <remarks>
/// <b>Values only, and no command.</b> Both entity submenus are built in code
/// and wire their own Click, because the two place differently - the Object
/// menu at the view centre and the viewport's at the right-click point - so a
/// command living here could only be right for one of them.
/// </remarks>
public sealed class EntityInsertItem
{
    internal EntityInsertItem(EntitySchema schema)
    {
        ClassName = schema.ClassName;
        Display = schema.DisplayName.Length > 0 ? schema.DisplayName : schema.ClassName;
        Group = schema.Group;
    }

    /// <summary>The wire name, as a map file spells it.</summary>
    public string ClassName { get; }

    /// <summary>The label the menu shows.</summary>
    public string Display { get; }

    /// <summary>The category the class files itself under, or empty.</summary>
    public string Group { get; }

    /// <summary>
    /// The class name beside its display label, for the entry's tooltip.
    /// </summary>
    /// <remarks>
    /// The class name is what a map file carries and what a wire names, so a
    /// menu that showed only a friendly label would hide the one string an
    /// author has to type elsewhere. It is a tooltip rather than a second
    /// column because a class whose display name is its class name would then
    /// print it twice.
    /// </remarks>
    public string Tip => Group.Length > 0
        ? $"{ClassName}  ({Group})"
        : ClassName;
}

/// <summary>
/// Turns a parsed schema catalogue into the Insert menu's entity entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>It takes an <see cref="EntitySchemaCatalog"/> and can take nothing
/// else</b>, which is the whole reason it is a function rather than a loop
/// inside the window. The catalogue is parsed from a <c>.sentdef</c> image;
/// <c>EntityCatalog.Shared</c> is the in-process registry of classes this build
/// can BUILD, and the two diverge the day a project ships a definitions file
/// this process has no C# for. A menu offering what the process can construct,
/// beside a panel describing what the file declares, is two answers to one
/// question.
/// </para>
/// <para>
/// <b>Ordered as the catalogue is, which is ordinal by class name.</b> Sorting
/// by the display label instead would put the same two classes in a different
/// order on a machine with a different locale, and re-sorting by group would
/// be a second opinion about a field the schema already carries.
/// </para>
/// </remarks>
public static class EntityInsertMenu
{
    /// <summary>
    /// The classes a point insert can place, in catalogue order.
    /// </summary>
    /// <param name="catalog">The parsed catalogue, or null before a session exists.</param>
    public static List<EntityInsertItem> Build(EntitySchemaCatalog? catalog)
    {
        var items = new List<EntityInsertItem>();
        if (catalog is null)
            return items;

        foreach (EntitySchema schema in catalog.Schemas)
        {
            // A BRUSH class gives behaviour to geometry the insert does not
            // create, so placing one from here would make a node that declares
            // it is a volume and carries no volume - invalid on its face, and
            // invisible in the viewport for a reason nothing on screen
            // explains. Those arrive by giving an existing brush a class, which
            // is a verb of its own and does not exist yet.
            if (schema.Placement == EntityPlacement.Brush)
                continue;

            items.Add(new EntityInsertItem(schema));
        }

        return items;
    }
}
