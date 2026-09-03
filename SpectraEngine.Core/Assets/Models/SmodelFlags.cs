using System;

namespace SpectraEngine.Core.Assets.Models;

/// <summary>
/// Whole-model properties, stored in the <c>.smodel</c> header's <c>u16</c> flag
/// word.
/// </summary>
/// <remarks>
/// <para><b>Only one of these is load-bearing at read time, and the split is
/// worth stating.</b> <see cref="Index32"/> is the sole fact in the header with
/// no other source: nothing else in the file says how wide an <c>IBUF</c>
/// element is, so it decides how the bytes are interpreted.
/// <see cref="HasSkeleton"/> and <see cref="HasCollision"/> are summaries of the
/// section table, which is the actual truth, so the reader derives presence from
/// the table and uses these two only to <em>cross-check</em> it. A flag that
/// disagrees with the table is a writer that was edited in one place and not the
/// other, and refusing there costs one comparison and catches the class outright.
/// </para>
/// <para>Values are append-only. Inserting a bit renumbers every flag after it,
/// and the failure is a model that loads with the wrong index width, which throws
/// nothing and reports nothing.</para>
/// </remarks>
[Flags]
public enum SmodelFlags : ushort
{
    /// <summary>No flag set: 32-bit indices off, no skeleton, no collision.</summary>
    None = 0,

    /// <summary>A <c>SKEL</c> section is present.</summary>
    HasSkeleton = 1 << 0,

    /// <summary>A <c>COLL</c> section is present.</summary>
    HasCollision = 1 << 1,

    /// <summary>
    /// <c>IBUF</c> holds <c>u32</c> elements rather than <c>u16</c>. The cooker
    /// picks 16-bit whenever the vertex count fits in one, so this is set for
    /// large models only.
    /// </summary>
    Index32 = 1 << 2,
}
