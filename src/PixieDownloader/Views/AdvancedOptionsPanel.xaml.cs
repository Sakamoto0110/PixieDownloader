using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PixieDownloader.ViewModels;

namespace PixieDownloader.Views;

public partial class AdvancedOptionsPanel : UserControl
{
    public AdvancedOptionsPanel() => InitializeComponent();

    /// <summary>"Pasta": Enter commits the typed path (the binding is LostFocus), Esc puts the stored one back.</summary>
    private void OnOutputDirectoryKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        if (e.Key == Key.Enter)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            e.Handled = true;
        }
    }

    /// <summary>
    /// "Personalizar": a field button puts its yt-dlp text where the caret is (replacing a selection). The
    /// buttons aren't focusable, so while the user is in the box the caret stays put; from anywhere else the
    /// field goes at the end. The edit goes through the VM as one undo step; the binding brings it back here.
    /// </summary>
    private void OnInsertToken(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TokenOption token || DataContext is not MainViewModel vm)
            return;
        var box = CustomTemplateBox;
        var (start, length) = box.IsKeyboardFocused ? (box.SelectionStart, box.SelectionLength) : (box.Text.Length, 0);
        vm.EditCustomTemplate(box.Text.Remove(start, length).Insert(start, token.Value));
        box.Focus();
        box.Select(Math.Min(start + token.Value.Length, box.Text.Length), 0);
    }
}
