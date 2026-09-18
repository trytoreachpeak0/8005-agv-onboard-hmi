using System.Collections.Specialized;
using System.ComponentModel;
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

    private async void OnWireToGateRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "请由已授权维护人员现场确认：车辆已经停稳，维修已经完成，所有目标仓门已锁好，开锁输出已复位，现场无人和障碍物。\n\n申请恢复只会继续服务端已授权的原操作，不会重新选择仓位。是否继续？",
            "申请恢复原操作",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        bool accepted = await _viewModel.RequestWireToGateRecoveryAsync();
        if (!accepted)
        {
            MessageBox.Show(
                "恢复申请未被接受。请检查授权凭据、现场安全条件和服务端恢复会话状态。",
                "恢复申请失败",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private async void OnLoadCancellationClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认车辆已停稳、目标仓门已锁好，并由授权人员确认本次装货应取消。\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；本站尚未录入子批时不会打开任何仓门。是否继续？",
                "取消装货",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestLoadCancellationAsync())
        {
            ShowRecoveryFailure("装货取消未被接受。请检查授权、车辆停稳信号和服务端状态。", "取消装货失败");
        }
    }

    private async void OnLoadCompensationClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认装货流程无法继续，并由授权维护人员确认需要将目标仓位全部清空。\n\n服务端授权后，系统才会执行清空动作。是否继续？",
                "补偿清空",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestLoadCompensationAsync())
        {
            ShowRecoveryFailure("补偿清空请求未被接受。请检查授权、恢复会话和服务端状态。", "补偿清空失败");
        }
    }

    private async void OnLoadCorrectionClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认上一笔装货结果需要修正，并由现场人员准备按‘取出后重新放入’的顺序操作。\n\n系统只执行服务端下发的原目标仓位修正命令。是否继续？",
                "修正装货",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestLoadCorrectionAsync())
        {
            ShowRecoveryFailure("装货修正请求未被接受。请检查上一笔装货记录和服务端状态。", "修正装货失败");
        }
    }

    private async void OnFaultCargoHandoffClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认目标仓存在故障货物，并由授权维护人员确认交接范围。\n\n系统会先等待服务端下发故障交接命令，再将目标仓位安全清空。是否继续？",
                "故障货物交接",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestFaultCargoHandoffAsync())
        {
            ShowRecoveryFailure("故障货物交接请求未被接受。请检查授权、恢复会话和服务端状态。", "故障交接失败");
        }
    }

    private async void OnForcedMechanicalRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认目标仓门无法电动解锁，并由授权维护人员现场确认需要人工撬开处理。\n\n强制机械恢复不证明仓位已清空、也不证明车辆可以恢复作业，车辆会保持需恢复状态，等待重新核对。是否继续？",
                "强制机械恢复",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestForcedMechanicalRecoveryAsync())
        {
            ShowRecoveryFailure("强制机械恢复请求未被接受。请检查授权、恢复会话和服务端状态。", "强制机械恢复失败");
        }
    }

    private async void OnConfirmForcedMechanicalRecoveryClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认：车辆已断电、抱闸隔离，并已由具备现场作业资质的人员以机械方式开锁或拆卸、取出货物。\n\n系统不会输出开锁。确认后上报「已机械隔离」，这些仓位随后标为物理状态未知，禁止操作，直到提交硬件恢复记录。是否确认？",
                "确认强制机械取出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.ConfirmForcedMechanicalRecoveryAsync())
        {
            ShowRecoveryFailure("强制机械取出结果未被服务端确认。请检查连接后再次确认，系统不会输出开锁。", "确认失败");
        }
    }

    private async void OnSubmitHardwareRecoveryRecordClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请确认强制机械取出涉及的全部仓位已修复，锁反馈、光幕和开锁输出信号正常。\n\n提交后服务端记录硬件恢复；车载端复核实时信号有效后解除这些仓位的「物理状态未知」，不会自动续作任何操作。是否提交？",
                "提交硬件恢复记录",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.SubmitHardwareRecoveryRecordAsync())
        {
            ShowRecoveryFailure("硬件恢复记录未生效。请填写说明、确认仓位信号有效后再提交。", "硬件恢复记录失败");
        }
    }

    private async void OnManualChargingReturnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请由授权维护人员确认手动充电已经结束、充电线已经拔除。\n\n系统只向服务端申请重新评估车辆业务资格，是否恢复接单以服务端的决定为准。是否继续？",
                "充电后返回服务",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestManualChargingReturnAsync())
        {
            ShowRecoveryFailure("返回服务请求未被服务端受理。请查看日志中的原因，车辆保持原状态。", "返回服务未受理");
        }
    }

    private static void ShowRecoveryFailure(string message, string title) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Information);

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.Logs.CollectionChanged -= OnLogsCollectionChanged;
            _viewModel.StopStationDepartureCountdown();
        }

        DataContextChanged -= OnDataContextChanged;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
