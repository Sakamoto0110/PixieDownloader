using System.Windows.Controls;

namespace PixieDownloader.Views;

/// <summary>The Plugins tab: one row per folder under plugins/, with enable / disable / uninstall. All logic is in the VM.</summary>
public partial class PluginsPanel : UserControl
{
    public PluginsPanel()
    {
        InitializeComponent();
    }
}
