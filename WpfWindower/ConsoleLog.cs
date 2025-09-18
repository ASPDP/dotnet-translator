using System;

namespace WpfWindower;

internal static class ConsoleLog
{
    public static void Info(string message) => Write(message, ConsoleColor.Gray);

    public static void Success(string message) => Write(message, ConsoleColor.Green);

    public static void Warning(string message) => Write(message, ConsoleColor.Yellow);

    public static void Error(string message) => Write(message, ConsoleColor.Red);

    public static void Highlight(string message) => Write(message, ConsoleColor.Cyan);

    private static void Write(string message, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(message);
        }
        finally
        {
            Console.ForegroundColor = originalColor;
        }
    }
}
