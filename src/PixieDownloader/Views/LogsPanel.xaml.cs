using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PixieDownloader.Views;

public partial class LogsPanel : UserControl
{
    public LogsPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (LogList.Items is INotifyCollectionChanged incc)
        {
            incc.CollectionChanged -= OnItemsChanged;
            incc.CollectionChanged += OnItemsChanged;
        }
    }

    // Follow the tail only when the user is already near the bottom — don't yank them down while reading.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add)
            return;

        var scroll = FindScrollViewer(LogList);
        if (scroll is null)
            return;

        if (scroll.VerticalOffset >= scroll.ScrollableHeight - 40)
            scroll.ScrollToEnd();
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }
        return null;
    }
}
