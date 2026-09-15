using System.Diagnostics.CodeAnalysis;

namespace PixieDownloader.Plugins;

/// <summary>
/// The one place a plugin can reach another: by capability id, always through the host, never by type. Each
/// registration is owned by the <see cref="PluginHost"/> that made it and goes away with it when that plugin
/// is disabled — which is exactly what makes a disabled plugin unreachable. Plugins may call this from any
/// thread, so whether the owner is still alive is decided under the same lock that removes its entries: a
/// registration racing the owner's shutdown either lands before it (and is removed by it) or is ignored.
/// </summary>
internal sealed class CapabilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (PluginHost Owner, Delegate Implementation)> _entries = new(StringComparer.Ordinal);

    /// <summary>Registers for a live owner; a shut-down one gets <see cref="NoRegistration"/> back, not an entry.</summary>
    public IDisposable Register(PluginHost owner, string id, Delegate implementation)
    {
        lock (_gate)
        {
            if (owner.IsShutDown)
                return NoRegistration.Instance;
            if (_entries.TryGetValue(id, out var existing))
                throw new InvalidOperationException($"A capacidade '{id}' já está registrada pelo plugin '{existing.Owner.Manifest.Id}'.");
            _entries[id] = (owner, implementation);
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

    /// <summary>Drops everything a host registered — disable, a failed Configure, app exit.</summary>
    public void RemoveAll(PluginHost owner)
    {
        lock (_gate)
        {
            foreach (var id in _entries.Where(e => ReferenceEquals(e.Value.Owner, owner)).Select(e => e.Key).ToList())
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

/// <summary>What a plugin gets back for a registration the host ignored (made after its shutdown): disposing it does nothing.</summary>
internal sealed class NoRegistration : IDisposable
{
    public static readonly NoRegistration Instance = new();
    public void Dispose() { }
}
