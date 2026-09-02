using System.ComponentModel;

namespace System.Runtime.CompilerServices;

/// <summary>
/// The marker the compiler requires for <c>init</c> accessors, which records
/// generate.
/// </summary>
/// <remarks>
/// netstandard2.0 predates it, so a generator that uses records has to declare
/// it. Internal, so it cannot collide with the same polyfill in another
/// assembly.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
internal static class IsExternalInit
{
}
