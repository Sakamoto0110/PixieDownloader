using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using PixieDownloader.Sdk;

namespace PixieDownloader.Plugins;

/// <summary>
/// A plugin folder without a <c>plugin.json</c>: everything the manifest would say is already in the
/// assembly's metadata, and <see cref="MetadataReader"/> reads it from the file without loading a byte of
/// code — so listing, refusing and the apiVersion check still happen before anything runs. The
/// conventions: the folder name is the id; the one <c>.dll</c> that references the Sdk is the plugin; the
/// one class implementing <see cref="IPixiePlugin"/> is the entry point; the assembly version is the
/// version; <c>AssemblyTitle</c> is the display name; and the Sdk version the assembly was compiled
/// against is the apiVersion — which is more honest than a hand-written field. A <c>plugin.json</c> is
/// still needed when any of that must differ: several entry classes, <c>dependsOn</c>, an id that isn't
/// the folder.
/// </summary>
internal static class PluginManifestReader
{
    private static readonly string SdkAssemblyName = typeof(IPixiePlugin).Assembly.GetName().Name!;
    private static readonly string SdkNamespace = typeof(IPixiePlugin).Namespace!;
    private static readonly string EntryInterface = nameof(IPixiePlugin);

    /// <summary>The manifest, or null with <paramref name="problem"/> saying why (for the Plugins tab, in Portuguese).</summary>
    public static PluginManifest? TryRead(string directory, out string? problem)
    {
        var id = Path.GetFileName(directory);
        var found = new List<PluginManifest>();
        string? ambiguous = null;
        bool sawSdkReference = false;

        foreach (var dll in Directory.GetFiles(directory, "*.dll").Order(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(dll);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata)
                    continue;
                var md = pe.GetMetadataReader();

                var sdk = FindSdkReference(md);
                if (sdk is null)
                    continue;   // a dependency of the plugin (TagLibSharp and the like), not the plugin
                sawSdkReference = true;

                var entries = FindEntryTypes(md);
                if (entries.Count == 0)
                    continue;   // references the Sdk but has no plugin class — a helper library
                if (entries.Count > 1)
                {
                    ambiguous = $"{Path.GetFileName(dll)} tem mais de uma classe que implementa {EntryInterface} ({string.Join(", ", entries)}) — diga qual no plugin.json";
                    continue;
                }

                var assembly = md.GetAssemblyDefinition();
                var name = ReadAssemblyTitle(md, assembly);
                found.Add(new PluginManifest
                {
                    Id = id,
                    Name = string.IsNullOrWhiteSpace(name) || name == md.GetString(assembly.Name) ? Capitalize(id) : name,
                    Version = assembly.Version.ToString(3),
                    ApiVersion = $"{sdk.Major}.{sdk.Minor}",
                    AssemblyFile = Path.GetFileName(dll),
                    EntryType = entries[0],
                });
            }
            catch (BadImageFormatException)
            {
                // not a .NET assembly (a native DLL the plugin carries) — skip
            }
        }

        if (found.Count == 1)
        {
            problem = null;
            return found[0];
        }
        problem = found.Count > 1
            ? $"mais de um .dll da pasta é um plugin ({string.Join(", ", found.Select(f => f.AssemblyFile))}) — diga qual no plugin.json"
            : ambiguous
              ?? (sawSdkReference
                    ? $"nenhuma classe implementa {EntryInterface} no .dll que referencia o {SdkAssemblyName}"
                    : $"sem plugin.json e sem um .dll que referencie o {SdkAssemblyName}");
        return null;
    }

    private static Version? FindSdkReference(MetadataReader md)
    {
        foreach (var handle in md.AssemblyReferences)
        {
            var reference = md.GetAssemblyReference(handle);
            if (md.GetString(reference.Name) == SdkAssemblyName)
                return reference.Version;
        }
        return null;
    }

    /// <summary>Public, concrete, top-level classes that implement <see cref="IPixiePlugin"/> directly.</summary>
    private static List<string> FindEntryTypes(MetadataReader md)
    {
        var entries = new List<string>();
        foreach (var handle in md.TypeDefinitions)
        {
            var type = md.GetTypeDefinition(handle);
            if ((type.Attributes & TypeAttributes.Interface) != 0 || (type.Attributes & TypeAttributes.Abstract) != 0)
                continue;
            if (!type.GetDeclaringType().IsNil)
                continue;   // nested — not an entry point
            foreach (var implHandle in type.GetInterfaceImplementations())
            {
                var impl = md.GetInterfaceImplementation(implHandle);
                if (impl.Interface.Kind != HandleKind.TypeReference)
                    continue;
                var iface = md.GetTypeReference((TypeReferenceHandle)impl.Interface);
                if (md.GetString(iface.Name) == EntryInterface && md.GetString(iface.Namespace) == SdkNamespace)
                {
                    var ns = md.GetString(type.Namespace);
                    entries.Add(ns.Length == 0 ? md.GetString(type.Name) : ns + "." + md.GetString(type.Name));
                    break;
                }
            }
        }
        return entries;
    }

    /// <summary>The <c>[assembly: AssemblyTitle("…")]</c> the SDK generates from <c>&lt;AssemblyTitle&gt;</c> (defaults to the assembly name).</summary>
    private static string? ReadAssemblyTitle(MetadataReader md, AssemblyDefinition assembly)
    {
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
                continue;
            var ctor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference)
                continue;
            var attributeType = md.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (md.GetString(attributeType.Name) != nameof(AssemblyTitleAttribute) || md.GetString(attributeType.Namespace) != "System.Reflection")
                continue;

            // Blob: 0x0001 prolog, then the one string argument.
            var blob = md.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1)
                return null;
            return blob.ReadSerializedString();
        }
        return null;
    }

    private static string Capitalize(string id) => id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id[1..];
}
