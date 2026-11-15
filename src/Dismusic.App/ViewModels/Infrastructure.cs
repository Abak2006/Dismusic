using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace Dismusic.App.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name!);
        return true;
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) => execute();
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>A selectable item (device, source…) identified by a key; null key = "none/default".</summary>
public sealed class Option(string? key, string name) : ObservableObject
{
    private string _name = name;
    public string? Key { get; } = key;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public override string ToString() => Name;
}

public static class CollectionSync
{
    /// <summary>
    /// Makes <paramref name="target"/> match <paramref name="desired"/> (by key, in order) with minimal
    /// inserts/moves/removes, so bound controls keep their selection and focus across refreshes.
    /// </summary>
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired, Func<T, string?> key, Action<T, T>? update = null)
    {
        for (int i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            var k = key(want);
            int existing = -1;
            for (int j = i; j < target.Count; j++)
                if (key(target[j]) == k) { existing = j; break; }

            if (existing < 0)
            {
                target.Insert(i, want);
                continue;
            }
            if (existing != i) target.Move(existing, i);
            update?.Invoke(target[i], want);
        }
        while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
    }
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
