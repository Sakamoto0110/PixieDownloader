using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PixieDownloader.ViewModels;

namespace PixieDownloader.Views;

/// <summary>
/// The queue tab. Reordering is plain mouse drag-and-drop on the rows: press on a row (not on its
/// button), move past the system drag threshold, drop on another row — the upper/lower half of the
/// target decides "before" or "after". The view model moves the row together with its linked partner.
/// </summary>
public partial class QueuePanel : UserControl
{
    private Point _dragStart;
    private DownloadJobViewModel? _dragCandidate;

    public QueuePanel() => InitializeComponent();

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        if (e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is not null)
            return;   // clicks on the row's button are not drags
        if (e.OriginalSource is DependencyObject src && FindAncestor<ListBoxItem>(src) is { DataContext: DownloadJobViewModel job })
        {
            _dragStart = e.GetPosition(JobList);
            _dragCandidate = job;
        }
    }

    private void OnRowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var delta = e.GetPosition(JobList) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop(JobList, new DataObject(typeof(DownloadJobViewModel), dragged), DragDropEffects.Move);
    }

    private void OnRowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(DownloadJobViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnRowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(DownloadJobViewModel)) is not DownloadJobViewModel dragged || Vm is not { } vm)
            return;

        if (e.OriginalSource is DependencyObject src && FindAncestor<ListBoxItem>(src) is { DataContext: DownloadJobViewModel target } item)
        {
            var insertAfter = e.GetPosition(item).Y > item.ActualHeight / 2;
            vm.MoveJob(dragged, target, insertAfter);
        }
        else if (vm.Jobs.Count > 0)
        {
            // Dropped on the empty area below the rows: send to the end.
            vm.MoveJob(dragged, vm.Jobs[^1], insertAfter: true);
        }
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var node = start; node is not null; node = GetParent(node))
        {
            if (node is T found)
                return found;
        }
        return null;
    }

    private static DependencyObject? GetParent(DependencyObject node)
        => node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
}
