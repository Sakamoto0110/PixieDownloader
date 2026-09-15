using System.IO;
using PixieDownloader.Plugins;

namespace PluginHost.Tests;

/// <summary>The rule for where a plugin's tab goes, walked through the owner's example: p1 p2 p3 p4 in the folder.</summary>
public class PluginTabOrderTests
{
    private const int First = 3;   // Baixar, Fila, Plugins come first

    private static readonly IReadOnlyList<InstalledPlugin> Folder =
        new[] { "p1", "p2", "p3", "p4" }.Select(id => InstalledPlugin.InFolder(Path.Combine(@"C:\app\plugins", id))).ToList();

    private static InstalledPlugin P(int n) => Folder[n - 1];

    [Fact]
    public void All_four_load_in_folder_order()
    {
        var showing = new List<string>();
        foreach (var plugin in Folder)
        {
            var index = PluginTabOrder.InsertIndex(First, Folder, plugin, showing);
            showing.Insert(index - First, plugin.Id);
        }
        Assert.Equal(["p1", "p2", "p3", "p4"], showing);
    }

    [Fact]
    public void A_plugin_that_comes_back_returns_to_its_own_place()
    {
        // p1 and p3 were disabled: the strip shows p2 p4.
        var showing = new List<string> { "p2", "p4" };

        // Re-enable p1: it goes before p2.
        var i1 = PluginTabOrder.InsertIndex(First, Folder, P(1), showing);
        Assert.Equal(First + 0, i1);
        showing.Insert(i1 - First, "p1");
        Assert.Equal(["p1", "p2", "p4"], showing);

        // Re-enable p3: between p2 and p4.
        var i3 = PluginTabOrder.InsertIndex(First, Folder, P(3), showing);
        Assert.Equal(First + 2, i3);
        showing.Insert(i3 - First, "p3");
        Assert.Equal(["p1", "p2", "p3", "p4"], showing);
    }

    [Fact]
    public void Tabs_that_are_not_plugins_do_not_count()
    {
        // Whatever else the strip holds (Baixar, Fila, Plugins, Debug, Logs) has no plugin id and never shifts the index.
        var index = PluginTabOrder.InsertIndex(First, Folder, P(4), ["p2"]);
        Assert.Equal(First + 1, index);
    }
}
