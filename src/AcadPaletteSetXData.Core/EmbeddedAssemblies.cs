using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace AcadPaletteSetXData.Core;

/// <summary>
/// Loads the third-party assemblies of the plugin (NCalc and its dependencies, issue #43) for AutoCAD.
/// <para>
/// The release <c>acadPaletteSetXDATA.dll</c> stays a single file: the build embeds every copy-local dependency as the
/// resource <c>Embedded.&lt;file name&gt;</c>, and this resolver loads it from memory. The development build deploys the
/// dependencies next to the plugin; AutoCAD has no binding redirects for a NETLOADed plugin, so a dependency asked
/// for with another version is resolved from that folder by its simple name.
/// </para>
/// <para>
/// Only names the plugin carries are resolved — an embedded resource or a file next to the plugin — every other
/// request is left to AutoCAD. Diagnostics: set the environment variable <c>ACADPALETTESETXDATA_TRACE_ASSEMBLIES=1</c>
/// to write each resolution to the trace listeners (the debugger output).
/// </para>
/// </summary>
public static class EmbeddedAssemblies
{
    /// <summary>The prefix of the manifest resource names of embedded assemblies.</summary>
    public const string ResourcePrefix = "Embedded.";

    private static readonly object Sync = new object();
    private static readonly Dictionary<string, Assembly> Loaded = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
    private static readonly bool Tracing = Environment.GetEnvironmentVariable("ACADPALETTESETXDATA_TRACE_ASSEMBLIES") == "1";
    private static bool registered;

    /// <summary>The simple names of the embedded assemblies; empty in the development build.</summary>
    public static IReadOnlyList<string> Names => Owner.GetManifestResourceNames()
        .Where(name => name.StartsWith(ResourcePrefix, StringComparison.Ordinal) && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Select(name => name.Substring(ResourcePrefix.Length, name.Length - ResourcePrefix.Length - 4))
        .ToArray();

    private static Assembly Owner => typeof(EmbeddedAssemblies).Assembly;

    /// <summary>Registers the resolver once; called by the module initializer before any type of the plugin runs.</summary>
    public static void Register()
    {
        lock (Sync)
        {
            if (registered) return;
            registered = true;
        }
        AppDomain.CurrentDomain.AssemblyResolve += (_, args) => Resolve(args.Name);
    }

    /// <summary>The assembly of the plugin for <paramref name="fullName"/>, or null when the plugin does not carry it.</summary>
    public static Assembly? Resolve(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return null;
        string? name;
        try { name = new AssemblyName(fullName).Name; }
        catch (Exception error) when (error is ArgumentException || error is FileLoadException) { return null; }
        if (string.IsNullOrEmpty(name) || name!.EndsWith(".resources", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(name, Owner.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
        lock (Sync)
        {
            if (Loaded.TryGetValue(name, out var cached)) return cached;
            Assembly? assembly = null;
            try
            {
                using (var stream = Owner.GetManifestResourceStream(ResourcePrefix + name + ".dll"))
                    if (stream != null)
                    {
                        var bytes = new byte[stream.Length];
                        var read = 0;
                        while (read < bytes.Length)
                        {
                            var count = stream.Read(bytes, read, bytes.Length - read);
                            if (count == 0) break;
                            read += count;
                        }
                        assembly = Assembly.Load(bytes);
                    }
                if (assembly is null && Directory() is string directory)
                {
                    var path = Path.Combine(directory, name + ".dll");
                    if (File.Exists(path)) assembly = Assembly.LoadFrom(path);
                }
            }
            catch (Exception error) when (error is IOException || error is BadImageFormatException || error is UnauthorizedAccessException)
            {
                Log(fullName + ": " + error.Message);
                return null;
            }
            if (assembly is null) return null;
            Log(fullName + " → " + assembly.FullName + (assembly.Location.Length == 0 ? " (embedded)" : " (" + assembly.Location + ")"));
            Loaded[name] = assembly;
            return assembly;
        }
    }

    private static string? Directory()
    {
        var location = Owner.Location;
        return string.IsNullOrEmpty(location) ? null : Path.GetDirectoryName(location);
    }

    private static void Log(string message)
    {
        if (Tracing) System.Diagnostics.Trace.WriteLine("AcadPaletteSetXData assemblies: " + message);
    }

    /// <summary>Registers the resolver when the plugin assembly is loaded, before NCalc is needed.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize() => Register();
}
