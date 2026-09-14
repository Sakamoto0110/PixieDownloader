using System.Windows;

namespace PixieDownloader.Sdk;

/// <summary>
/// Implemented next to <see cref="IPixiePlugin"/>, on the same class, by a plugin that has a tab. The host adds one
/// tab per plugin, headed by <see cref="TabHeader"/>, and removes it the moment the plugin is disabled. It is a
/// separate interface because not every plugin has a UI.
/// </summary>
public interface IUiContribution
{
    /// <summary>The tab title, as shown in the tab strip.</summary>
    string TabHeader { get; }

    /// <summary>Builds the tab content. Called once, on the UI thread, after <see cref="IPixiePlugin.Configure"/>.</summary>
    FrameworkElement CreateView();
}
