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

    /// <summary>
    /// 复位当前锁存的严重安全故障（onboard-hmi#171）。确认框问的是维护人员用眼睛能判断的三件事，
    /// 不是系统状态：门、开锁指示、货物。系统那一半由 <c>OnboardController.ClearFatalFaultAsync</c>
    /// 自己复核，不成立时横幅上会写清是哪一条没过。
    /// </summary>
    private async void OnClearFatalFaultClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        MessageBoxResult confirmation = MessageBox.Show(
            "请先现场确认：所有仓门均已可靠锁闭，没有仓门正在开启，界面显示的货物状态与实际一致。\n\n"
            + "复位之后本界面会重新允许扫码开门，复位人会记入日志。是否继续？",
            "复位严重安全故障",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation == MessageBoxResult.Yes)
        {
            _ = await _viewModel.ClearFatalFaultAsync();
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
        // 本站多条需求时确认框复述所选那一条，免得按下去才发现选错了行（批次7-14）。
        if (_viewModel is null
            || MessageBox.Show(
                "请确认车辆已停稳、目标仓门已锁好，并由授权人员确认本次装货应取消。\n\n"
                + _viewModel.LoadCancellationConfirmationDetailText
                + "\n\n系统只会执行服务端授权的目标仓位清空，不会重新选择仓位；本站尚未录入子批时不会打开任何仓门。是否继续？",
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
                "请确认：车辆已断电、抱闸隔离，并已由具备现场作业资质的人员以机械方式开锁或拆卸、取出货物，货物已交给所填的接收人。\n\n系统不会输出开锁。确认后先记录本次确认与货物交接记录，再上报服务端；交接记录只证明货物已救出并完成交接，不证明仓位已空、也不证明车辆可以恢复作业。车辆保持需恢复，这些仓位保持禁止操作，直到提交硬件恢复记录。是否确认？",
                "确认强制机械取出",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.ConfirmForcedMechanicalRecoveryAsync())
        {
            // 没有上报成功的原因只有业务层知道——缺交接记录、子批号待再次确认、还在等服务端确认——它已经写在操作提示那一行，
            // 这里照抄那一行，不另编一句（8005-agv-onboard-hmi#216）。
            ShowRecoveryFailure(
                "强制机械取出结果尚未由服务端确认。" + _viewModel.Guidance + "\n\n车辆保持需恢复，这些仓位保持禁止操作；系统不会输出开锁。",
                "强制机械取出未上报");
        }
    }

    private async void OnSubmitHardwareRecoveryRecordClick(object sender, RoutedEventArgs e)
    {
        // 有一份上次没送达的记录时，这一按重发的是那一份，不是框里现在的文字（PR #248 审查）：对话框把要重发的内容写出来。
        string? pending = _viewModel?.PendingHardwareRecoveryRecordObservations;
        string resend = pending is null
            ? string.Empty
            : $"\n\n将重发上次未送达的记录，说明为：「{pending}」。本次框里新填的文字不会发出；服务端只认这份记录的原内容。";
        if (_viewModel is null
            || MessageBox.Show(
                "请确认需要恢复的全部仓位（强制机械取出的仓位，或因门锁未证明被扣的仓位）已修复，锁反馈、光幕和开锁输出信号正常。\n\n"
                    + "提交后服务端记录硬件恢复：强制机械取出的仓位由车载端复核实时信号有效后解除「物理状态未知」；被扣的仓位由服务端"
                    + "核对新读数、做一次扣车解除检查后决定是否解除扣车。不会自动续作任何操作。" + resend + "\n\n是否提交？",
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

    /// <summary>
    /// 维修放行（onboard-hmi#219）：申请解除门锁未证明的扣车。只开会话、选放行动作，不开任何仓门。
    /// </summary>
    private async void OnHardwareRepairReleaseClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || MessageBox.Show(
                "请由授权维护人员确认：被扣仓位的门锁已经修好，仓内无货。\n\n申请维修放行不会开任何仓门；获准后请填写维修记录并提交，"
                    + "服务端核对新读数、做一次扣车解除检查后决定是否解除扣车。是否继续？",
                "维修放行",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.RequestHardwareRepairReleaseAsync())
        {
            ShowRecoveryFailure("维修放行未获服务端授权。请查看日志中的原因，车辆保持扣车。", "维修放行未受理");
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

    /// <summary>
    /// 人工清桩确认（8005-agv-onboard-hmi#221）。对话框依据的那一份在弹出之前取定，确认之后原样交回。
    /// </summary>
    /// <remarks>
    /// 对话框开着的时候界面照常刷新：服务端可能换了原充电桩，上一次提交的结果也可能回来。这里只读一次
    /// <c>StationClearance</c>，正文与交回去的 <c>Prompt</c> 出自同一份记录；业务服务发现它已经不是当前这一份就拒绝，不发。
    /// 确认之后再去读一次，就成了「对话框写的是甲、发出去的是乙」（<c>WireToGateStationClearanceTests</c> 有一条结构守卫）。
    /// </remarks>
    private async void OnConfirmStationClearanceClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.StationClearance is not { Prompt: { } prompt, ConfirmationText: var confirmationText }
            || MessageBox.Show(
                confirmationText,
                "确认清桩",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.ConfirmStationClearanceAsync(prompt))
        {
            ShowRecoveryFailure("清桩确认没有得到服务端的确认。原因见入口下方的结果一行和操作记录；站点状态以服务端为准。", "清桩确认未完成");
        }
    }

    /// <summary>
    /// 扣住的服务端恢复命令，确认执行（8005-agv-onboard-hmi#239）。对话框之前读一次视图模型的入口，确认之后原样交回
    /// 那一次读到的 <c>Prompt</c>：对话框开着时视图模型照常刷新，换掉的只是屏幕，不是这里要确认的那一条。
    /// </summary>
    private async void OnConfirmHeldRecoveryCommandClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.HeldRecoveryCommand is not { Prompt: { } prompt }
            || MessageBox.Show(
                prompt.Text.Trim()
                    + "\n\n确认人已在车旁、仓门附近安全，现在执行这条命令？车辆会给上述仓门发开锁信号。",
                "确认执行服务端恢复命令",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.ConfirmHeldRecoveryCommandAsync(prompt))
        {
            ShowRecoveryFailure("这条命令已不再等待确认，没有执行。原因见操作记录。", "未执行");
        }
    }

    /// <summary>
    /// 扣住的服务端恢复命令，不执行（8005-agv-onboard-hmi#239）。与 <see cref="OnConfirmHeldRecoveryCommandClick"/> 同形。
    /// </summary>
    /// <remarks>
    /// 装货修正的「不执行」由业务服务要求按两次：第一次只把后果写进说明、不回复服务端，第二次才回复，而且只对第一次
    /// 显示的那条命令有效。对话框正文里带着同一句后果。第一次按下的结局是「等第二次按下」，不弹失败；第二次按下没能
    /// 回复服务端时照常弹出（onboard-hmi#239 审查备注）。
    /// </remarks>
    private async void OnDeclineHeldRecoveryCommandClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel?.HeldRecoveryCommand is not { Prompt: { } prompt }
            || MessageBox.Show(
                prompt.Text.Trim()
                    + (prompt.DeclineConsequence.Length > 0 ? "\n\n" + prompt.DeclineConsequence : string.Empty)
                    + "\n\n确定不执行这条命令？车辆不会开锁，并向服务端报告未执行；服务端将结束本次恢复，如仍需处理要重新发起。",
                "不执行服务端恢复命令",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (await _viewModel.DeclineHeldRecoveryCommandAsync(prompt) == HeldRecoveryDeclineOutcome.NotAnswered)
        {
            ShowRecoveryFailure(
                "没有向服务端回复：命令已不再等待确认，或者结果没能写入发件箱。原因见入口下方说明和操作记录。",
                "未回复");
        }
    }

    /// <summary>
    /// 现场确认充不上（8005-agv-onboard-hmi#222）。对话框依据的是被按下那个按钮自己的选项，确认之后原样交回它的
    /// <c>Prompt</c>。
    /// </summary>
    /// <remarks>
    /// 按钮的 DataContext 就是那一个 <c>UnableToChargeOption</c>：正文与交回去的 <c>Prompt</c> 出自同一个对象，对话框开着时
    /// 视图模型照常刷新也换不掉它；业务服务发现它已经不是当前的一份就拒绝，不发。这里不去读视图模型的
    /// <c>UnableToCharge</c>（<c>WireToGateUnableToChargeTests</c> 有一条结构守卫）。
    /// </remarks>
    private async void OnConfirmUnableToChargeClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel is null
            || (sender as FrameworkElement)?.DataContext is not UnableToChargeOption { Prompt: var prompt, ConfirmationText: var confirmationText }
            || MessageBox.Show(
                confirmationText,
                "现场确认充不上",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        if (!await _viewModel.ConfirmUnableToChargeAsync(prompt))
        {
            ShowRecoveryFailure("现场确认充不上没有得到服务端的确认。原因见入口下方的结果一行和操作记录；车辆接下来怎么走以服务端为准。", "现场确认未完成");
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
