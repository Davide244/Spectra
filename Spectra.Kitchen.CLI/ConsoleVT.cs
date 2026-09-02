using System.Runtime.InteropServices;

namespace Spectra.Kitchen.CLI;

/// <summary>
/// Turns on virtual-terminal processing so an ANSI sequence renders as a colour
/// rather than as the literal text of the sequence.
/// </summary>
/// <remarks>
/// A copy of <c>ssc</c>'s. Both tools are AOT-published, so the imports are
/// <c>LibraryImport</c> rather than <c>DllImport</c>.
/// </remarks>
internal static partial class ConsoleVT
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static bool TryEnableForStderr() => TryEnable(STD_ERROR_HANDLE);
    public static bool TryEnableForStdout() => TryEnable(STD_OUTPUT_HANDLE);

    private static bool TryEnable(int stdHandle)
    {
        if (!OperatingSystem.IsWindows())
            return true;

        var handle = GetStdHandle(stdHandle);
        if (handle == IntPtr.Zero || handle == InvalidHandle)
            return false;

        if (!GetConsoleMode(handle, out uint mode))
            return false;

        if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
            return true;

        return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
