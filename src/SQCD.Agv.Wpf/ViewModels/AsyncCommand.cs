using System.Windows.Input;

namespace SQCD.Agv.Wpf.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool> _canExecute;
    private readonly Action<Exception> _onError;
    private bool _isRunning;

    public AsyncCommand(Func<Task> execute, Func<bool> canExecute, Action<Exception> onError)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _onError = onError ?? throw new ArgumentNullException(nameof(onError));
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isRunning && _canExecute();

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 程序退出或严重安全故障锁存时，控制器会主动取消当前命令。
            // 取消属于受控停止，不再作为新的界面异常重复上报。
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _onError(exception);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
