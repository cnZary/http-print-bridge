using System.Runtime.InteropServices;

namespace HttpPrintBridge.Win32;

/// <summary>
/// Optional console-window hiding for runs that should stay out of the way
/// (shortcuts, autostart). Only ever hides a console this process owns.
/// </summary>
internal static partial class ConsoleWindow
{
    private const int SW_HIDE = 0;

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static unsafe partial uint GetConsoleProcessList(uint* processList, uint processCount);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    /// <summary>
    /// Hides the console window when this process is its only owner. A console shared
    /// with a parent shell is left alone — hiding it would hide the user's terminal too.
    /// No-op when there is no console at all (redirected output, service context).
    /// </summary>
    public static unsafe void HideOwnConsole()
    {
        nint hwnd = GetConsoleWindow();
        if (hwnd == 0)
            return;

        // Returns 1 only when we are the sole process attached to this console.
        uint* pids = stackalloc uint[2];
        if (GetConsoleProcessList(pids, 2) != 1)
            return;

        ShowWindow(hwnd, SW_HIDE);
    }
}
