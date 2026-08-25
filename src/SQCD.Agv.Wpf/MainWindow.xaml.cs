using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf;

public partial class MainWindow : Window
{
    private MainViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
        }

        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.Logs.CollectionChanged += OnLogsCollectionChanged;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusScanInput();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CanSubmit) && _viewModel?.CanSubmit == true)
        {
            _ = Dispatcher.BeginInvoke(FocusScanInput);
        }
    }

    private void FocusScanInput()
    {
        if (ScanTextBox.IsEnabled)
        {
            ScanTextBox.Focus();
            ScanTextBox.SelectAll();
        }
    }

    private void OnLogsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (LogListBox.Items.Count > 0)
            {
                LogListBox.ScrollIntoView(LogListBox.Items[^1]);
            }
        }, DispatcherPriority.Background);
    }

    private async void OnSafetyReviewClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "请先现场确认：所有仓门均已可靠锁闭，界面显示的货物状态与实际一致。\n\n安全复核不会清空货物状态，是否继续？",
            "启动安全复核",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
        {
            _ = await _viewModel.ConfirmSafeStartupStateAsync();
        }
    }

    private async void OnReopenOperationClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"即将重新打开{_viewModel.RecoverySlotName}。\n\n请确认仓门附近无人遮挡，并准备继续完成装卸。是否继续？",
            "重新打开仓门",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool accepted = await _viewModel.RequestReopenCurrentOperationAsync();
        if (!accepted)
        {
            MessageBox.Show(
                "当前状态已经变化，不能重新打开仓门。请按照界面最新提示操作。",
                "无法重新开门",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void OnCancelOperationClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            $"确定取消{_viewModel.RecoverySlotName}当前未完成的装卸操作吗？\n\n取消前请确认仓门已经锁好，仓内货物状态没有发生变化。",
            "取消本次操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool accepted = await _viewModel.RequestCancelCurrentOperationAsync();
        if (!accepted)
        {
            MessageBox.Show(
                "当前状态已经变化，不能取消本次操作。请按照界面最新提示操作。",
                "无法取消操作",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void OnRetryPendingResultClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool acknowledged = await _viewModel.RetryPendingResultAsync();
        if (!acknowledged)
        {
            MessageBox.Show(
                "任务系统仍未确认结果。系统会保留原结果并在连接恢复后继续重试，请勿重复扫码或重新操作仓门。",
                "结果尚未确认",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
        }

        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
