using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Pixie.Library;

public partial class LibraryView : UserControl
{
    private const string DragFormat = "Pixie.Library.Rows";

    private Point _dragStart;
    private LibraryRow? _dragRow;

    public LibraryView()
    {
        InitializeComponent();
    }

    private LibraryTabViewModel? Vm => DataContext as LibraryTabViewModel;

    // Loaded fires every time the tab is selected (the TabControl swaps its content in and out): that is
    // "opening the tab", the moment the roadmap's state machine looks at the folders.
    private void OnLoaded(object sender, RoutedEventArgs e) => Vm?.OnTabOpened();

    // Esc clears the search. Done here rather than with a KeyBinding: the window claims Escape before the binding sees it.
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm is not { } vm)
            return;
        vm.FilterText = "";
        e.Handled = true;
    }

    // "Adicionar ▾": the button's own menu, under it, on a left click.
    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    // The name band just appeared: the caret goes there, the text selected (a rename shows the current name).
    private void OnNameBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true && sender is TextBox box)
            box.Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); });
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => Vm?.SetSelection(FileList.SelectedItems.OfType<LibraryRow>());

    // ───── Context menu: built for the row under the mouse ─────

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Vm is not { } vm || RowAt(e.OriginalSource as DependencyObject) is not { } row)
        {
            e.Handled = true;
            return;
        }
        if (!FileList.SelectedItems.Contains(row))
        {
            FileList.SelectedItems.Clear();
            FileList.SelectedItem = row;
        }
        var targets = vm.TargetsOf(row);
        var menu = new ContextMenu { Style = (Style)FindResource("LibraryMenu") };
        menu.Resources[typeof(MenuItem)] = FindResource("LibraryMenuItem");
        menu.Resources[typeof(Separator)] = FindResource("LibraryMenuSeparator");

        if (row.IsFile)
        {
            menu.Items.Add(Item("Abrir", () => vm.OpenCommand.Execute(row)));
            menu.Items.Add(Item("Pasta", () => vm.ShowInFolderCommand.Execute(row)));
        }
        else if (row.IsFolder)
        {
            menu.Items.Add(Item(row.IsExpanded ? "Fechar" : "Abrir", () => vm.ToggleExpandCommand.Execute(row)));
            menu.Items.Add(Item("Abrir pasta no Explorer", () => vm.ShowInFolderCommand.Execute(row)));
        }
        else if (row.IsGroup)
        {
            menu.Items.Add(Item(row.IsExpanded ? "Fechar" : "Abrir", () => vm.ToggleExpandCommand.Execute(row)));
        }

        if (row.IsPhysical && !row.IsTop)
        {
            menu.Items.Add(new Separator());
            var groups = vm.GroupChoicesFor(targets);
            var addTo = new MenuItem { Header = targets.Count > 1 ? $"Adicionar {targets.Count} itens a" : "Adicionar a" };
            if (groups.Count == 0)
                addTo.Items.Add(new MenuItem { Header = "(nenhum grupo — Adicionar ▾ › Criar grupo…)", IsEnabled = false });
            foreach (var choice in groups)
            {
                var item = new MenuItem { Header = choice.Label, IsCheckable = false, IsChecked = choice.Checked };
                var id = choice.Id;
                item.Click += (_, _) => vm.AddToGroup(id, targets);
                addTo.Items.Add(item);
            }
            menu.Items.Add(addTo);
            if (row.InGroup is { } group && row.ItemId is not null)
                menu.Items.Add(Item($"Remover de {group.Name}", () => vm.RemoveFromGroup(row)));
            menu.Items.Add(Item(targets.Count > 1 ? $"Mover {targets.Count} itens fisicamente…" : "Mover fisicamente…", () => vm.MovePhysically(targets)));
        }
        if (row.IsFolder && (row.IsTop || vm.IsInsideStorageRoot(row)))
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Criar pasta aqui…", () => vm.CreateFolderIn(row)));
        }
        if (row.IsFolder && row.IsSourceRoot && row.Item is { IsSource: true })
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remover do catálogo", () => vm.RemoveSource(row)));
        }
        if (row.IsGroup)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Renomear…", () => vm.RenameGroup(row)));
            menu.Items.Add(Item("Novo subgrupo…", () => vm.CreateSubgroup(row)));
            menu.Items.Add(Item("Excluir grupo", () => vm.DeleteGroup(row)));
        }
        if (row.IsMissing)
        {
            menu.Items.Add(Item("Localizar…", () => vm.Relocate(row)));
            if (row.InGroup is { } group)
                menu.Items.Add(Item($"Remover de {group.Name}", () => vm.RemoveFromGroup(row)));
        }

        FileList.ContextMenu = menu;
        menu.PlacementTarget = FileList;
        menu.IsOpen = true;
        e.Handled = true;

        static MenuItem Item(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }
    }

    // ───── Drag and drop: rows onto a group ─────

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(FileList);
        _dragRow = e.OriginalSource is DependencyObject source && !IsInteractive(source) ? RowAt(source) : null;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is not { } row || Vm is not { } vm)
            return;
        var moved = e.GetPosition(FileList) - _dragStart;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;
        var rows = vm.TargetsOf(row).Where(r => !r.IsTop && (r.IsPhysical || r.IsGroup)).ToList();
        _dragRow = null;
        if (rows.Count == 0)
            return;
        DragDrop.DoDragDrop(FileList, new DataObject(DragFormat, rows), DragDropEffects.Link | DragDropEffects.Move);
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DropEffect(e, out _);
        e.Handled = true;
    }

    private void OnListDrop(object sender, DragEventArgs e)
    {
        var effect = DropEffect(e, out var target);
        e.Handled = true;
        if (effect == DragDropEffects.None || Vm is not { } vm || target is null || e.Data.GetData(DragFormat) is not List<LibraryRow> rows)
            return;
        if (rows.Count == 1 && rows[0].IsGroup)
            vm.MoveGroup(rows[0], target.IsTop ? null : target);
        else if (target.GroupId is { } groupId)
            vm.AddToGroup(groupId, rows);
    }

    /// <summary>A drop lands on a group (files and folders become members; a group changes parent) or, for a group, on "Tudo" (to the top). Nothing else takes a drop: a physical folder is not "move".</summary>
    private DragDropEffects DropEffect(DragEventArgs e, out LibraryRow? target)
    {
        target = null;
        if (Vm is not { } vm || e.Data.GetData(DragFormat) is not List<LibraryRow> rows || rows.Count == 0)
            return DragDropEffects.None;
        var point = e.GetPosition(FileList);
        target = RowAt(FileList.InputHitTest(point) as DependencyObject);
        if (target is null)
            return DragDropEffects.None;
        if (rows.Count == 1 && rows[0].IsGroup)
            return (target.IsGroup || target.IsTop) && vm.WhyNotMoveGroup(rows[0], target.IsTop ? null : target) is null ? DragDropEffects.Move : DragDropEffects.None;
        return target.IsGroup && rows.All(r => r.IsPhysical) ? DragDropEffects.Link : DragDropEffects.None;
    }

    // ───── helpers ─────

    private LibraryRow? RowAt(DependencyObject? source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ListBoxItem item)
                return item.DataContext as LibraryRow;
            if (current is ListBox)
                return null;
        }
        return null;
    }

    /// <summary>A click on a button or the chevron is a click, never the start of a drag.</summary>
    private static bool IsInteractive(DependencyObject source)
    {
        for (var current = source; current is not null && current is not ListBoxItem; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ButtonBase)
                return true;
        }
        return false;
    }
}
