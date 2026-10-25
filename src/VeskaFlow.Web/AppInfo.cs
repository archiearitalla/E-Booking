using System.Reflection;

namespace VeskaFlow.Web;

/// <summary>Single source of truth for the version shown in the footer, About page and /healthz.</summary>
public static class AppInfo
{
    public const string Name = "VeskaFlow";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public static string Build { get; } = ResolveBuild();

    private static string ResolveBuild()
    {
        try
        {
            var location = typeof(AppInfo).Assembly.Location;
            return string.IsNullOrEmpty(location)
                ? "dev"
                : File.GetLastWriteTimeUtc(location).ToString("yyyyMMdd");
        }
        catch (IOException) { return "dev"; }
    }
}
