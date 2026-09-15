namespace PixieDownloader.Plugins;

/// <summary>
/// Where a plugin's tab goes in the strip. The strip is Baixar · Fila · Plugins · [one tab per plugin with a
/// UI, in the catalog's order — the folder's order] · Debug (when enabled) · Logs. A plugin that comes back
/// after being disabled returns to its own place among the others, not to the end: with p1 p2 p3 p4 installed
/// and p2 p4 showing, enabling p3 gives p2 p3 p4 and enabling p1 gives p1 p2 p3 p4.
/// </summary>
internal static class PluginTabOrder
{
    /// <summary>
    /// The index to insert <paramref name="plugin"/>'s tab at: <paramref name="firstPluginTab"/> plus the number of
    /// plugins that precede it in <paramref name="catalogOrder"/> and currently have a tab (<paramref name="tabsShowing"/>, by id).
    /// </summary>
    public static int InsertIndex(int firstPluginTab, IReadOnlyList<InstalledPlugin> catalogOrder, InstalledPlugin plugin, IEnumerable<string> tabsShowing)
    {
        var showing = new HashSet<string>(tabsShowing, StringComparer.OrdinalIgnoreCase);
        int before = 0;
        foreach (var other in catalogOrder)
        {
            if (ReferenceEquals(other, plugin))
                break;
            if (showing.Contains(other.Id))
                before++;
        }
        return firstPluginTab + before;
    }
}
