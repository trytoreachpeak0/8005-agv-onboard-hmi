namespace SQCD.Agv.Wpf.ScreenState;

/// <summary>
/// What the operator's screen shows and lets them press, read from the view model (T1a of
/// <c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c>, onboard-hmi#313). Built by <see cref="HmiScreenStateBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Content boundary (research section 4.1).</b> Nothing here is a sublot, an input box's text, a credential, or text
/// that can contain one: input boxes carry only whether they are empty; dialogs carry only their literal title; the
/// operator record carries only time and kind (several records name a sublot); worklist rows carry the demand and side,
/// not the sublot. The banner's guidance line is not recorded for the same reason -- it can name the sublot being
/// verified, or the one the operator typed into the forced handoff box.
/// </para>
/// <para>
/// <b>Names.</b> A control's <c>VisibilityBinding</c>/<c>IsEnabledBinding</c>/<c>TextBinding</c> is the binding path
/// written in <c>MainWindow.xaml</c>, so "button X is bound to property Y" and "the snapshot's X is Y" can be compared
/// mechanically. <c>HmiScreenStateXamlCoverageArchitectureTests</c> holds the two sides equal.
/// </para>
/// <para>
/// Writer metadata (content revision, process start, <c>ACTIVE</c>/<c>DISABLED</c>) belongs to the writer, T2.
/// </para>
/// </remarks>
public sealed record HmiScreenState(
    DateTimeOffset BuiltAt,
    string ProgramVersion,
    IReadOnlyList<HmiButtonState> Buttons,
    IReadOnlyList<HmiInputState> Inputs,
    HmiModalState Modals,
    HmiBannerState Banner,
    HmiOperatorNoticeState? OperatorNotice,
    IReadOnlyList<HmiRawCodeState> RawCodes,
    IReadOnlyList<HmiJourneyPlanLegState> JourneyPlanLegs,
    IReadOnlyList<HmiWorklistRowState> WorklistRows,
    IReadOnlyList<string> RecoveryDemandChoices,
    string? SelectedRecoveryDemandChoice,
    IReadOnlyList<HmiLockerState> Lockers,
    HmiStationDepartureCountdownState StationDepartureCountdown,
    HmiCargoHoldingState CargoHolding,
    IReadOnlyList<HmiOperatorRecordState> RecentOperatorRecords);

/// <summary>
/// One button. <see cref="Visible"/> is the value of its <c>Visibility</c> binding ("visible at the binding layer"),
/// regardless of whether the window is covered or minimised, and <c>true</c> when it has none. <see cref="Enabled"/> is
/// the value of its <c>IsEnabled</c> binding, or <c>CanExecute</c> for a <c>Command</c> button, or, for the template
/// button, whether there is any option (each option is one button, listed in <see cref="Options"/>).
/// </summary>
/// <param name="Key">The <c>Click</c> handler, or the <c>Command</c> binding path for a command button.</param>
/// <param name="Content">The button's literal caption; null for the template button, whose captions are the options'.</param>
public sealed record HmiButtonState(
    string Key,
    string? AutomationId,
    string? Content,
    string? VisibilityBinding,
    string? IsEnabledBinding,
    string? CommandBinding,
    bool Visible,
    bool Enabled,
    IReadOnlyList<HmiOptionButtonState>? Options);

/// <summary>One option of the "现场确认充不上" template button.</summary>
public sealed record HmiOptionButtonState(string AutomationId, string Label);

/// <summary>
/// One editable input box: never its text. <see cref="Enabled"/> is <c>true</c> for a box with no <c>IsEnabled</c>
/// binding; <see cref="KeyCommandEnabled"/> is the <c>CanExecute</c> of a <c>KeyBinding</c> on the box (the scanner's
/// Enter), null when it has none.
/// </summary>
/// <param name="Key">The box's <c>AutomationId</c>, or its <c>x:Name</c> when it has none.</param>
public sealed record HmiInputState(
    string Key,
    string? VisibilityBinding,
    string? IsEnabledBinding,
    string TextBinding,
    string? KeyCommandBinding,
    bool Visible,
    bool Enabled,
    bool HasText,
    bool? KeyCommandEnabled);

/// <summary>The modal dialogs open over the window, outermost first; only titles, never bodies.</summary>
public sealed record HmiModalState(int Depth, IReadOnlyList<OpenModalDialog> Open)
{
    public OpenModalDialog? Top => Open.Count == 0 ? null : Open[^1];
}

/// <summary>
/// The status banner. <see cref="StateText"/> is drawn from a fixed vocabulary on both writing paths;
/// <see cref="GuidanceHeldByOperatorNotice"/> says the guidance line currently shows <see cref="HmiScreenState.OperatorNotice"/>.
/// </summary>
public sealed record HmiBannerState(
    string StateText,
    bool HasWarning,
    bool HasError,
    HmiBannerSource Source,
    bool GuidanceHeldByOperatorNotice);

/// <summary>The operator notice holding the guidance line: a registered rejection code and its wording, until when.</summary>
public sealed record HmiOperatorNoticeState(string Text, DateTimeOffset Until);

/// <summary>
/// A raw code the screen carries in UIA's <c>ItemStatus</c>, keyed by the element's <c>AutomationId</c>.
/// <see cref="Visible"/> is the value of the nearest <c>Visibility</c> binding on the element or above it.
/// </summary>
public sealed record HmiRawCodeState(
    string AutomationId,
    string ItemStatusBinding,
    string? VisibilityBinding,
    string Value,
    bool Visible);

/// <summary>A journey plan row: its <c>ItemStatus</c> (sequence|purpose|state) and station.</summary>
public sealed record HmiJourneyPlanLegState(string ItemStatus, string StationId);

/// <summary>A worklist row without its sublot.</summary>
public sealed record HmiWorklistRowState(string DemandId, string SideCode);

/// <summary>A slot card.</summary>
public sealed record HmiLockerState(int PhysicalNumber, bool IsKnown, bool IsLocked, bool HasCargo, bool IsTarget);

/// <summary>
/// The station departure countdown: tier, caption and deadline, never the per-second text or the blink phase, so the
/// content does not change every second.
/// </summary>
public sealed record HmiStationDepartureCountdownState(
    bool Visible,
    string Tier,
    string Label,
    bool IsStationWorkCountdown,
    DateTimeOffset? DeadlineAt);

/// <summary>The cargo holding line: status code and the "latest departure" time it shows, never the per-second text.</summary>
public sealed record HmiCargoHoldingState(bool Visible, string Status, DateTimeOffset? LatestDepartureAt);

/// <summary>One line of the on-screen operator record: when and what kind; the message is left out (it can name a sublot).</summary>
public sealed record HmiOperatorRecordState(DateTimeOffset Timestamp, string Kind);
