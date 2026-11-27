namespace ScanRelay.ProbeTools;

/// <summary>What the probe tools share. Each tool is its own program, so this file is compiled into each of them.</summary>
internal static class ProbeConsole
{
    /// <summary>The value after a named option, such as the folder after "--out", or null when it is not given.</summary>
    public static string? Option(string[] args, string name) =>
        Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

    /// <summary>Asks for a password or PIN without showing what is typed.</summary>
    public static string ReadHidden(string prompt)
    {
        Console.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0) text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return text.ToString();
    }
}
