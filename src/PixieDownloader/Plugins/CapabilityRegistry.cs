using System.Diagnostics.CodeAnalysis;

namespace PixieDownloader.Plugins;

/// <summary>
/// The one place a plugin can reach another: by capability id, always through the host, never by type. Each
/// registration is owned by the plugin that made it and goes away with it when that plugin is disabled —
/// which is exactly what makes a disabled plugin unreachable. Plugins may call this from any thread.
/// </summary>
internal sealed class CapabilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string PluginId, Delegate Implementation)> _entries = new(StringComparer.Ordinal);

    public IDisposable Register(string pluginId, string id, Delegate implementation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var existing))
                throw new InvalidOperationException($"A capacidade '{id}' já está registrada pelo plugin '{existing.PluginId}'.");
            _entries[id] = (pluginId, implementation);
        }
        return new Registration(this, id, implementation);
    }

    public bool TryGet(string id, [NotNullWhen(true)] out Delegate? implementation)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                implementation = entry.Implementation;
                return true;
            }
        }
        implementation = null;
        return false;
    }

    /// <summary>Drops everything a plugin registered — disable and app exit.</summary>
    public void RemoveAll(string pluginId)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Where(e => e.Value.PluginId == pluginId).Select(e => e.Key).ToList())
                _entries.Remove(id);
        }
    }

    private void Remove(string id, Delegate implementation)
    {
        lock (_gate)
        {
            // Only the registration that put it there may take it out: a stale Dispose after a re-register is a no-op.
            if (_entries.TryGetValue(id, out var entry) && ReferenceEquals(entry.Implementation, implementation))
                _entries.Remove(id);
        }
    }

    private sealed class Registration(CapabilityRegistry owner, string id, Delegate implementation) : IDisposable
    {
        public void Dispose() => owner.Remove(id, implementation);
    }
}
