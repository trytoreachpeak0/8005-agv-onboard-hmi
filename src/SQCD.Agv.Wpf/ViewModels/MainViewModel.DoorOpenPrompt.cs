namespace SQCD.Agv.Wpf.ViewModels;

public enum DoorOpenPromptTier
{
    None,
    Waiting,
    Overdue
}

public sealed partial class MainViewModel
{
    public bool HasDoorOpenPrompt => false;

    public DoorOpenPromptTier DoorOpenPromptTier => DoorOpenPromptTier.None;

    public string DoorOpenPromptHeadline => string.Empty;

    public string DoorOpenPromptDetail => string.Empty;

    public bool DoorOpenPromptDimmed => false;

    internal void RefreshDoorOpenPrompt()
    {
    }
}
