using System.Reflection;

namespace Hawk.Web.Infrastructure;

internal static class AppVersion
{
    public static string Get()
    {
        var asm = typeof(AppVersion).Assembly;

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+', StringComparison.Ordinal);
            return plus > 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
