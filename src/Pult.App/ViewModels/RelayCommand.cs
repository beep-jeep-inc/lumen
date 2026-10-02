using System.Windows.Input;

namespace Pult.App.ViewModels;

public sealed class RelayCommand(Func<Task> run, Func<bool>? can = null) : ICommand
{
    private bool _busy;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !_busy && (can?.Invoke() ?? true);
    public async void Execute(object? p)
    {
        _busy = true; CommandManager.InvalidateRequerySuggested();
        try { await run(); }
        catch (Exception e) { Services.Log.Error("команда", e); }
        finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
    }
    public static RelayCommand Sync(Action a, Func<bool>? can = null) => new(() => { a(); return Task.CompletedTask; }, can);
}
