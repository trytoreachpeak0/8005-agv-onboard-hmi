using SQCD.Agv.Core;

namespace SQCD.Agv.Wpf.ViewModels;

/// <summary>
/// 仓位区里的一组卡片。分组与标题取自 <see cref="SlotGroupPresentation"/>，这里只负责绑定。
/// </summary>
public sealed class SlotGroupViewModel : ViewModelBase
{
    private bool _isTarget;

    public SlotGroupViewModel(SlotGroup group, IReadOnlyList<LockerCardViewModel> lockers)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
        Lockers = lockers ?? throw new ArgumentNullException(nameof(lockers));
    }

    public SlotGroup Group { get; }

    public string Title => Group.Title;

    public bool IsUnknownSide => Group.Side == SlotSide.Unknown;

    public IReadOnlyList<LockerCardViewModel> Lockers { get; }

    public bool IsTarget
    {
        get => _isTarget;
        set => SetProperty(ref _isTarget, value);
    }
}
