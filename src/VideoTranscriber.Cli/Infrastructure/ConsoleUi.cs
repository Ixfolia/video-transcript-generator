using System.Runtime.InteropServices;

namespace VideoTranscriber.Infrastructure;

/// <summary>Small helpers for consistent, readable console output.</summary>
public static class ConsoleUi
{
    /// <summary>Serializes console writes between progress bars and messages.</summary>
    internal static readonly object SyncRoot = new();

    public static void Info(string message) => WriteLine(message, null);

    public static void Step(string message) => WriteLine(message, ConsoleColor.Cyan);

    public static void Success(string message) => WriteLine(message, ConsoleColor.Green);

    public static void Warning(string message) => WriteLine(message, ConsoleColor.Yellow, Console.Error);

    public static void Error(string message, string? hint = null)
    {
        WriteLine("Error: " + message, ConsoleColor.Red, Console.Error);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            WriteLine(hint, null, Console.Error);
        }
    }

    public static void Dim(string message) => WriteLine(message, ConsoleColor.DarkGray);

    /// <summary>Asks a yes/no question. Returns <paramref name="defaultAnswer"/> on Enter, false when input isn't interactive.</summary>
    public static bool Confirm(string question, bool defaultAnswer)
    {
        if (Console.IsInputRedirected)
        {
            return false;
        }

        Console.Write($"{question} [{(defaultAnswer ? "Y/n" : "y/N")}] ");
        string? answer = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(answer))
        {
            return defaultAnswer;
        }

        return answer.StartsWith('y') || answer.StartsWith('Y');
    }

    /// <summary>
    /// True when this process owns its console window, i.e. it was started by double-clicking the exe or by dropping a
    /// file onto it in Explorer. In that case the window closes as soon as we exit, so we pause first.
    /// </summary>
    public static bool IsStandaloneConsoleWindow()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return false;
        }

        try
        {
            uint[] processIds = new uint[2];
            return GetConsoleProcessList(processIds, (uint)processIds.Length) == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static void PauseBeforeExit()
    {
        Console.WriteLine();
        Console.Write("Press any key to close this window...");
        try
        {
            Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No keyboard available.
        }
    }

    internal static void WriteLine(string message, ConsoleColor? color, TextWriter? writer = null)
    {
        writer ??= Console.Out;
        lock (SyncRoot)
        {
            ProgressBar.ClearActiveLine();
            if (color is { } c && !IsColorDisabled(writer))
            {
                Console.ForegroundColor = c;
                writer.WriteLine(message);
                Console.ResetColor();
            }
            else
            {
                writer.WriteLine(message);
            }

            ProgressBar.RedrawActive();
        }
    }

    private static bool IsColorDisabled(TextWriter writer) =>
        Environment.GetEnvironmentVariable("NO_COLOR") is not null
        || (writer == Console.Out ? Console.IsOutputRedirected : Console.IsErrorRedirected);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);
}
