using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Reflection;
using System.Windows.Input;
using SQCD.Agv.Wpf.ViewModels;

namespace SQCD.Agv.Wpf.ScreenState;

/// <summary>
/// Reads one <see cref="HmiScreenState"/> off the view model (onboard-hmi#313, T1a). Read-only: it calls property getters
/// and <c>ICommand.CanExecute</c>, nothing else; <c>HmiScreenStateBuilderReadOnlyArchitectureTests</c> scans this file
/// for anything that writes, executes or touches focus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread.</b> The view model is written only on the WPF dispatcher thread, so this must run there too: the writer of
/// T2 queues it with <c>DispatcherPriority.Background</c>. Nothing calls it in the product yet. It does not catch: the
/// isolation of research section 4.3 item 1 (catch everything, disable the snapshot, write <c>DISABLED</c>) wraps each
/// dispatcher entry point of T2, which is where a failure has somewhere to go.
/// </para>
/// <para>
/// <b>The operator notice at its expiry.</b> <c>ActiveOperatorNotice</c> is null from the instant its hold ends, but the
/// guidance line keeps the notice's words until the next banner publish replaces them -- the next IO snapshot, about
/// 100 ms by default. A snapshot taken in that gap reports no notice and <c>GuidanceHeldByOperatorNotice</c> false while
/// the screen still shows the notice. Left as it is for T1a; T3's cross-check should expect it (onboard-hmi#313 review L5).
/// </para>
/// <para>
/// <b>Collections are copied by index off a count read first</b>, the way the G2 harness copies the operator log: on the
/// dispatcher nothing appends in between, and in the G2 harness, which has no dispatcher, a copy that loses a race
/// throws and is retried by the caller.
/// </para>
/// </remarks>
internal static class HmiScreenStateBuilder
{
    /// <summary>How many of the newest operator record lines are reported.</summary>
    internal const int RecentOperatorRecordCount = 20;

    private static readonly ConcurrentDictionary<(Type, string), PropertyInfo[]> Paths = new();

    private static readonly string ProgramVersion =
        typeof(HmiScreenStateBuilder).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(HmiScreenStateBuilder).Assembly.GetName().Version?.ToString()
        ?? string.Empty;

    internal static HmiScreenState Build(MainViewModel viewModel, IModalDialogRegistry modals)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(modals);

        IReadOnlyList<OpenModalDialog> open = modals.Read();
        string? notice = viewModel.ActiveOperatorNotice;
        DateTimeOffset? noticeUntil = viewModel.ActiveOperatorNoticeUntil;

        return new HmiScreenState(
            viewModel.Clock.Now,
            ProgramVersion,
            [.. HmiScreenBindings.Buttons.Select(binding => Button(viewModel, binding))],
            [.. HmiScreenBindings.Inputs.Select(binding => Input(viewModel, binding))],
            new HmiModalState(open.Count, open),
            new HmiBannerState(
                viewModel.StateText,
                viewModel.HasWarning,
                viewModel.HasError,
                viewModel.BannerSource,
                notice is not null && string.Equals(viewModel.Guidance, notice, StringComparison.Ordinal)),
            notice is not null && noticeUntil is { } until ? new HmiOperatorNoticeState(notice, until) : null,
            [.. HmiScreenBindings.RawCodes.Select(binding => RawCode(viewModel, binding))],
            [.. Copy(viewModel.JourneyPlanLegs).Select(row => new HmiJourneyPlanLegState(row.ItemStatus, row.StationId))],
            [.. Copy(viewModel.WorklistItems).Select(row => new HmiWorklistRowState(row.DemandId, row.SideCode))],
            [.. Copy(viewModel.RecoveryDemandChoices).Select(row => row.DemandId)],
            viewModel.SelectedRecoveryDemandChoice?.DemandId,
            [.. Copy(viewModel.Lockers).Select(locker => new HmiLockerState(
                locker.PhysicalNumber,
                locker.IsKnown,
                locker.IsLocked,
                locker.HasCargo,
                locker.IsTarget))],
            new HmiStationDepartureCountdownState(
                viewModel.HasStationDepartureCountdown,
                Convert.ToString(viewModel.StationDepartureCountdownTier, CultureInfo.InvariantCulture) ?? string.Empty,
                viewModel.StationDepartureCountdownLabel,
                viewModel.IsStationWorkCountdown,
                viewModel.StationDepartureDeadlineAt),
            new HmiCargoHoldingState(
                viewModel.HasCargoHoldingCountdown,
                viewModel.CargoHoldingCountdownStatus,
                viewModel.CargoHoldingLatestDepartureAt),
            [.. Copy(viewModel.Logs)
                .TakeLast(RecentOperatorRecordCount)
                .Select(line => new HmiOperatorRecordState(line.Timestamp, line.Kind.ToString()))]);
    }

    internal static HmiButtonState Button(MainViewModel viewModel, HmiScreenBindings.ButtonBinding binding)
    {
        bool visible = binding.Visibility is not { } visibility || (bool)Read(viewModel, visibility)!;
        switch (binding.Kind)
        {
            case HmiScreenBindings.ButtonKind.Command:
                return State(visible, ((ICommand)Read(viewModel, binding.Command!)!).CanExecute(null), null);

            case HmiScreenBindings.ButtonKind.Template:
                HmiOptionButtonState[] options = [.. CopyItems((IEnumerable)Read(viewModel, binding.ItemsSource!)!)
                    .Cast<UnableToChargeOption>()
                    .Select(option => new HmiOptionButtonState(option.AutomationId, option.Label))];
                return State(visible, options.Length > 0, options);

            default:
                // No IsEnabled binding means WPF's default, enabled -- the same reading as an input box without one.
                return State(visible, binding.IsEnabled is not { } isEnabled || (bool)Read(viewModel, isEnabled)!, null);
        }

        HmiButtonState State(bool isVisible, bool enabled, IReadOnlyList<HmiOptionButtonState>? options) =>
            new(
                binding.Key,
                binding.AutomationId,
                binding.Content,
                binding.Visibility,
                binding.IsEnabled,
                binding.Command,
                isVisible,
                enabled,
                options);
    }

    private static HmiInputState Input(MainViewModel viewModel, HmiScreenBindings.InputBinding binding) =>
        new(
            binding.Key,
            binding.Visibility,
            binding.IsEnabled,
            binding.Text,
            binding.KeyCommand,
            binding.Visibility is not { } visibility || (bool)Read(viewModel, visibility)!,
            binding.IsEnabled is not { } isEnabled || (bool)Read(viewModel, isEnabled)!,
            // Only whether there is text: the content is the operator's (a sublot, a name, free text).
            !string.IsNullOrEmpty((string?)Read(viewModel, binding.Text)),
            binding.KeyCommand is { } command ? ((ICommand)Read(viewModel, command)!).CanExecute(null) : null);

    private static HmiRawCodeState RawCode(MainViewModel viewModel, HmiScreenBindings.RawCodeBinding binding) =>
        new(
            binding.AutomationId,
            binding.ItemStatus,
            binding.Visibility,
            Convert.ToString(Read(viewModel, binding.ItemStatus), CultureInfo.InvariantCulture) ?? string.Empty,
            binding.Visibility is not { } visibility || (bool)Read(viewModel, visibility)!);

    /// <summary>Evaluates a binding path (<c>A</c> or <c>A.B</c>) the way WPF does: public instance property getters.</summary>
    private static object? Read(object source, string path)
    {
        object? value = source;
        foreach (PropertyInfo property in Paths.GetOrAdd((source.GetType(), path), Resolve))
        {
            if (value is null)
            {
                return null;
            }

            value = property.GetValue(value);
        }

        return value;
    }

    private static PropertyInfo[] Resolve((Type Type, string Path) key)
    {
        Type type = key.Type;
        List<PropertyInfo> chain = [];
        foreach (string name in key.Path.Split('.'))
        {
            PropertyInfo property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{type.Name} has no public property {name} (binding path {key.Path}).");
            chain.Add(property);
            type = property.PropertyType;
        }

        return [.. chain];
    }

    private static T[] Copy<T>(ObservableCollection<T> items)
    {
        int count = items.Count;
        T[] copy = new T[count];
        for (int index = 0; index < count; index++)
        {
            copy[index] = items[index] ?? throw new InvalidOperationException("a view-model collection was read mid-change");
        }

        return copy;
    }

    private static object[] CopyItems(IEnumerable items) =>
        items is IList list ? [.. Enumerable.Range(0, list.Count).Select(index => list[index]!)] : [.. items.Cast<object>()];
}
