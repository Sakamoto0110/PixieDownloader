using System.Reflection;
using System.Runtime.Loader;
using PixieDownloader.Sdk;
using YtDlpCore;

namespace PixieDownloader.Plugins;

/// <summary>
/// One load context per plugin folder. Whatever the plugin brings along (its own NuGet packages, native
/// DLLs) is resolved from its folder through its <c>.deps.json</c>; the two assemblies host and plugin must
/// see as the <em>same</em> types — the Sdk and the core — are never loaded here: returning <see langword="null"/>
/// for them sends the request to the default context, i.e. the host's copy. Without that, the plugin's
/// <c>IPixiePlugin</c> and the host's would be two distinct types and the cast would fail with a message
/// nobody can read. Not collectible, on purpose: an unload was never promised (WPF registers dependency
/// properties process-wide and has no way to take them back), and a collectible context that nothing
/// references any more gets unloaded by the GC behind WPF's back — WPF resolves <c>pack://</c> resources by
/// assembly <em>name</em> through process-wide caches, so a view of a plugin could then fail with "does not
/// have a resource identified by the URI". Seen as a 2-in-10 flake in the host tests, where catalogs come
/// and go; the app keeps every context alive anyway.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    // The names come from the types, not from string literals, so renaming an assembly can't break this quietly.
    private static readonly HashSet<string> SharedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        typeof(IPixiePlugin).Assembly.GetName().Name!,
        typeof(IYtDlpService).Assembly.GetName().Name!,
    };

    private readonly AssemblyDependencyResolver _resolver;

    public PluginLoadContext(string pluginAssemblyPath, string pluginId)
        : base(name: "plugin:" + pluginId, isCollectible: false)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    /// <summary>True for the assemblies that must come from the host, whatever a plugin folder contains.</summary>
    public static bool IsShared(AssemblyName name) => name.Name is { } n && SharedAssemblies.Contains(n);

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName))
            return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
