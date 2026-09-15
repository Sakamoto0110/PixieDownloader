using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Pixie.Library;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    // Loaded fires every time the tab is selected (the TabControl swaps its content in and out): that is
    // "opening the tab", the moment the roadmap's state machine looks at the folders.
    private void OnLoaded(object sender, RoutedEventArgs e)
        => (DataContext as LibraryTabViewModel)?.OnTabOpened();

    // Esc clears the search. Done here rather than with a KeyBinding: the window claims Escape before the binding sees it.
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not LibraryTabViewModel vm)
            return;
        vm.FilterText = "";
        e.Handled = true;
    }
}
