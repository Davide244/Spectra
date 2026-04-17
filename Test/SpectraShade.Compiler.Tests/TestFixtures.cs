using System;
using System.IO;

namespace SpectraShade.Compiler.Tests;

internal static class TestFixtures
{
    private static readonly string BaseDir = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string Load(string name) => File.ReadAllText(Path.Combine(BaseDir, name));
}
