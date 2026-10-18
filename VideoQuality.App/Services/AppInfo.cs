using System.Reflection;

namespace VideoQuality.App.Services;

public static class AppInfo
{
    /// <summary>The build's version without the commit suffix (the SDK always stamps one).</summary>
    public static string Version { get; } = "v" + typeof(AppInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
