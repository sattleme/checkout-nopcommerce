using System.Reflection;

namespace Nop.Plugin.Payments.Sattleme.Tests.Support;

/// <summary>Where the repository and the plugin's built archive are (written into this assembly by the build).</summary>
public static class Repository
{
    /// <summary>The repository's root.</summary>
    public static string Root => Metadata("RepositoryRoot");

    /// <summary>The plugin's sources.</summary>
    public static string Plugin => Path.Combine(Root, "src", "Nop.Plugin.Payments.Sattleme");

    /// <summary>artifacts/&lt;configuration&gt;/: the archive, and the folder it holds (stage/Payments.Sattleme).</summary>
    public static string Package => Metadata("PluginPackage");

    /// <summary>The folder the archive holds, as the build left it.</summary>
    public static string PackageFolder => Path.Combine(Package, "stage", "Payments.Sattleme");

    /// <summary>The plugin's version.</summary>
    public static string Version => Metadata("Version");

    /// <summary>The archive.</summary>
    public static string Archive => Path.Combine(Package, $"Payments.Sattleme-{Version}.zip");

    /// <summary>A file of the repository.</summary>
    public static string File(params string[] parts)
    {
        return Path.Combine([Root, .. parts]);
    }

    private static string Metadata(string key)
    {
        return typeof(Repository).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == key).Value
            ?? throw new InvalidOperationException(key);
    }
}