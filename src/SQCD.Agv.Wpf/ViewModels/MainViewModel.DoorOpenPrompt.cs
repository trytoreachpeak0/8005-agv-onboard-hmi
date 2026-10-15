namespace SQCD.Agv.Wpf.ViewModels;

public enum DoorOpenPromptTier
{
    None,
    Waiting,
    Overdue
}

public sealed partial class MainViewModel
{
    private bool _hasDoorOpenPrompt;
    private DoorOpenPromptTier _doorOpenPromptTier;
    private string _doorOpenPromptHeadline = string.Empty;
    private string _doorOpenPromptDetail = string.Empty;
    private bool _doorOpenPromptDimmed;

    public bool HasDoorOpenPrompt
    {
        get => _hasDoorOpenPrompt;
        private set => SetProperty(ref _hasDoorOpenPrompt, value);
    }

    public DoorOpenPromptTier DoorOpenPromptTier
    {
        get => _doorOpenPromptTier;
        private set => SetProperty(ref _doorOpenPromptTier, value);
    }

    public string DoorOpenPromptHeadline
    {
        get => _doorOpenPromptHeadline;
        private set => SetProperty(ref _doorOpenPromptHeadline, value);
    }

    public string DoorOpenPromptDetail
    {
        get => _doorOpenPromptDetail;
        private set => SetProperty(ref _doorOpenPromptDetail, value);
    }

    public bool DoorOpenPromptDimmed
    {
        get => _doorOpenPromptDimmed;
        private set => SetProperty(ref _doorOpenPromptDimmed, value);
    }

    internal void RefreshDoorOpenPrompt() => RunOnUiThread(() => HasDoorOpenPrompt = false);
}
