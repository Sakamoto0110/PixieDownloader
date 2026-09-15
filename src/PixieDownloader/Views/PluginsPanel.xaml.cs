using System.Windows.Controls;
using PixieDownloader.ViewModels;

namespace PixieDownloader.Views;

/// <summary>
/// The Plugins tab: one row per folder under plugins/, with enable / disable / uninstall, and the official
/// plugins of the latest release below. All logic is in the VM; the only thing the view decides is when the
/// catalog is worth fetching — the first time the tab is actually shown (Loaded fires when a tab is selected).
/// </summary>
public partial class PluginsPanel : UserControl
{
    public PluginsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MainViewModel)?.EnsureStoreLoaded();
    }
}
