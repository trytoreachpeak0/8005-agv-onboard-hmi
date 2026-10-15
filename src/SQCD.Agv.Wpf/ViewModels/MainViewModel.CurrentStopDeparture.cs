using SQCD.Agv.Application;
using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 「出发」入口与待送点摘要（协议 4.0.0 候选：待送点 CP-0012、<c>8005-agv-onboard-hmi#349</c>；本站已装完的机台取货
/// CP-0014、<c>8005-agv-onboard-hmi#352</c>，那里只有按钮与确认框，没有待送点摘要）。
/// </summary>
/// <remarks>
/// <para>
/// 与人工清桩确认同形：业务服务的入口视图整份读回来，操作员确认的那一份整份交回去，接线在 <c>CurrentStopDepartureWiring</c>，
/// G2 夹具调的是同一个方法。
/// </para>
/// <para>
/// <b>它不经那十个恢复入口的锁存守卫</b>：锁存期间会话不就绪，业务视图自己就给出「暂不能出发」，按钮留在原位但按不动，
/// 而不是整个消失——待送点上操作员看得到这个按钮，比找不到它更清楚发生了什么。
/// </para>
/// <para>
/// <b>已装篮数与空仓数读自仓位读数</b>（仓位卡片的「有货／空仓」），待装条数读自屏上清单。只显示，不判断：车满不满、
/// 装不装得下，由服务端判。
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    private CurrentStopDepartureDisplay _currentStopDeparture = CurrentStopDepartureDisplay.Empty;
    private Func<WireToGateCurrentStopDepartureView>? _currentStopDepartureView;
    private Func<WireToGateCurrentStopDeparturePrompt, CancellationToken, Task<bool>>? _currentStopDepartureRequester;

    public CurrentStopDepartureDisplay CurrentStopDeparture
    {
        get => _currentStopDeparture;
        private set
        {
            if (SetProperty(ref _currentStopDeparture, value))
            {
                // 「取消装货」按钮上的字跟着待送点走（8005-agv-onboard-hmi#348）。
                OnPropertyChanged(nameof(LoadCancellationCaption));
            }
        }
    }

    internal void ConfigureCurrentStopDeparture(
        Func<WireToGateCurrentStopDepartureView> view,
        Func<WireToGateCurrentStopDeparturePrompt, CancellationToken, Task<bool>> requester)
    {
        _currentStopDepartureView = view ?? throw new ArgumentNullException(nameof(view));
        _currentStopDepartureRequester = requester ?? throw new ArgumentNullException(nameof(requester));
        RunOnUiThread(RefreshCurrentStopDepartureCore);
    }

    /// <summary>
    /// 操作员在确认框里点「是」之后调用，<paramref name="shown"/> 是确认框依据的那一份
    /// <see cref="CurrentStopDepartureDisplay.Prompt"/>。业务服务核对它仍是当前这一份才发；不是就不发。
    /// </summary>
    public async Task<bool> RequestCurrentStopDepartureAsync(
        WireToGateCurrentStopDeparturePrompt shown,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(shown);
        if (_currentStopDepartureRequester is null)
        {
            return false;
        }

        Task<bool> request = _currentStopDepartureRequester(shown, cancellationToken);
        // 业务服务在第一次 await 之前就记下了「正在发」，这一次读会立刻把按钮收起来。
        RunOnUiThread(RefreshCurrentStopDepartureCore);
        try
        {
            return await request.ConfigureAwait(true);
        }
        finally
        {
            RunOnUiThread(RefreshCurrentStopDepartureCore);
        }
    }

    private void RefreshCurrentStopDepartureCore()
    {
        if (_currentStopDepartureView?.Invoke() is not { Shown: true } view)
        {
            CurrentStopDeparture = CurrentStopDepartureDisplay.Empty;
            return;
        }

        (int loaded, int frontEmpty, int rearEmpty, int unknown) = ReadSlotOccupancy();
        int unloadedItems = view.Prompt?.UnloadedItemCount ?? WorklistItems.Count;
        bool staging = !view.AtMachinePickupStop;
        string notice = view.Prompt is null
            ? WireToGateCurrentStopDepartureText.UnavailableText(view.Unavailability)
            : string.Empty;
        string status = WireToGateCurrentStopDepartureText.StatusText(view.Last);
        string confirmation = view.Prompt switch
        {
            null => string.Empty,
            { } prompt when !staging => WireToGateCurrentStopDepartureText.MachineConfirmationText(prompt.PickupStopsAfter),
            _ => WireToGateCurrentStopDepartureText.ConfirmationText(loaded, unloadedItems)
        };
        CurrentStopDeparture = new CurrentStopDepartureDisplay(
            true,
            view.Prompt is not null,
            view.Prompt,
            confirmation,
            notice.Length > 0,
            notice,
            status.Length > 0,
            status,
            WireToGateCurrentStopDepartureText.Status(view.Last),
            staging
                ? WireToGateCurrentStopDepartureText.SummaryText(loaded, frontEmpty, rearEmpty, unknown, unloadedItems)
                : string.Empty,
            notice.Length > 0 ? view.Unavailability.ToString() : string.Empty,
            staging
                ? WireToGateCurrentStopDepartureText.SummaryCode(loaded, frontEmpty, rearEmpty, unknown, unloadedItems)
                : string.Empty)
        {
            AtStagingPickupStop = staging
        };
    }

    /// <summary>
    /// 仓位读数汇总：有货的仓数，前后两侧各自的空仓数，读数未知（或不属于前后任何一侧）的仓数。
    /// </summary>
    private (int Loaded, int FrontEmpty, int RearEmpty, int Unknown) ReadSlotOccupancy()
    {
        int loaded = 0;
        int frontEmpty = 0;
        int rearEmpty = 0;
        int unknown = 0;
        foreach (SlotGroupViewModel group in SlotGroups)
        {
            foreach (LockerCardViewModel locker in group.Lockers)
            {
                if (!locker.IsKnown)
                {
                    unknown++;
                }
                else if (locker.HasCargo)
                {
                    loaded++;
                }
                else if (group.Group.Side == SlotSide.Front)
                {
                    frontEmpty++;
                }
                else if (group.Group.Side == SlotSide.Rear)
                {
                    rearEmpty++;
                }
                else
                {
                    unknown++;
                }
            }
        }

        return (loaded, frontEmpty, rearEmpty, unknown);
    }
}
