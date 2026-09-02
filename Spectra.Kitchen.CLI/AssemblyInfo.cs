using System.Runtime.CompilerServices;

// The CLI's own surface is what the tests assert on: the exit code and the exact
// stderr line an IDE parses. Reaching it through Program.Run rather than by
// spawning scook keeps those tests as fast as every other test here, and keeps
// them able to see the writers.
[assembly: InternalsVisibleTo("Spectra.Kitchen.Tests")]
