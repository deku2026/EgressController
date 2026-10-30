using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
namespace EgressController.Desktop.ViewModels;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Raise(name); return true; }
    protected void Raise([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
public sealed class UiCommand(Action execute, Func<bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke() ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(); }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
public sealed class AsyncUiCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await execute(); } finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
public sealed record Choice(string Id, string Name, string Detail = "")
{
    public override string ToString() => Name;
}

public sealed class BoundedEvents : System.Collections.ObjectModel.ObservableCollection<EventRow>
{
    protected override void InsertItem(int index, EventRow item) { base.InsertItem(index, item); while (Count > 200) RemoveAt(Count - 1); }
}
