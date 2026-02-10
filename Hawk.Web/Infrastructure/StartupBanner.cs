using System.Reflection;

namespace Hawk.Web.Infrastructure;

internal static class StartupBanner
{
    public static void Write()
    {
        var art = """
 _   _   ___  _   _ _  __
| | | | / _ \| | | | |/ /
| |_| || (_) | |_| | ' / 
|  _  | \__, |\__,_| . \ 
|_| |_|   /_/     |_|\\_\\
""";

        WriteBold(art.TrimEnd());
        Console.WriteLine($"v{GetVersion()}");
        Console.WriteLine();
    }

    private static void WriteBold(string text)
    {
        if (!Console.IsOutputRedirected)
            Console.Write("\x1b[1m"); // ANSI bold on

        Console.WriteLine(text);

        if (!Console.IsOutputRedirected)
            Console.Write("\x1b[0m"); // ANSI reset
    }

    private static string GetVersion()
    {
        var asm = typeof(StartupBanner).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            // Common pattern: "0.9.5+<gitsha>".
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}

