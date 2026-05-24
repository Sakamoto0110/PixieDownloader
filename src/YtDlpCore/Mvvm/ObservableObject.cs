using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace YtDlpCore;

/// <summary>
/// Base mínima de INotifyPropertyChanged. Substitui o ObservableObject do CommunityToolkit.
/// Chame <see cref="SetProperty"/> no setter de uma propriedade: ele só dispara PropertyChanged
/// quando o valor realmente muda, e o nome da propriedade é inferido por [CallerMemberName].
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
