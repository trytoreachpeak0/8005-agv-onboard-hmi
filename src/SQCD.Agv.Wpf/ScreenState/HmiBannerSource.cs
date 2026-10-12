namespace SQCD.Agv.Wpf.ScreenState;

/// <summary>
/// Which path last wrote the status banner of <c>MainViewModel</c>. The research named two writers; there are four
/// (onboard-hmi#313), and <c>HmiBannerWriteSiteArchitectureTests</c> holds each of them to recording itself here.
/// </summary>
public enum HmiBannerSource
{
    /// <summary>Nothing has written the banner yet.</summary>
    None,

    /// <summary><c>ApplySnapshot</c>: the controller's state and error code. A latched fatal fault is shown from here.</summary>
    ControllerSnapshot,

    /// <summary><c>WireToGateHmiPresentation.Create</c>, from the session and the slot operation.</summary>
    WireToGatePresentation,

    /// <summary><c>ReportOperatorRejection</c>: a business rejection put in front of the operator.</summary>
    OperatorRejection,

    /// <summary><c>HandleCommandError</c>: a UI command failed and latched <c>UI_COMMAND_FAILED</c>.</summary>
    CommandFailure,
}
