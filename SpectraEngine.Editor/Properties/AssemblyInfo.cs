using System.Runtime.CompilerServices;

// The shell is an application, so nothing it exposes is API. Two pieces inside
// it are pure functions worth pinning all the same: the scene tree's replay of
// the engine's change log, and the virtual-key table. Both would fail silently
// rather than loudly, which is exactly the kind of thing a test is for.
[assembly: InternalsVisibleTo("SpectraEngine.Editor.Tests")]
