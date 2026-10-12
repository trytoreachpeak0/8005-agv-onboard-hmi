using System.Runtime.CompilerServices;
using System.Windows;

namespace SQCD.Agv.Wpf;

/// <summary>
/// The only way this application shows a <see cref="MessageBox"/>: the same call, with a record of which dialogs are open
/// around it, so the screen-state snapshot can say "the operator can only press the dialog right now"
/// (<c>docs/W2G_HMI_OBSERVABILITY_RESEARCH.md</c> section 4.1, onboard-hmi#313).
/// </summary>
/// <remarks>
/// <para>
/// <b>Transparent by construction.</b> Each overload hands its arguments, unchanged, to the matching
/// <see cref="MessageBox.Show(string, string, MessageBoxButton, MessageBoxImage)"/> overload and returns its result
/// unchanged. The record is kept around the call in a <c>try/finally</c>; the dialog itself is not touched.
/// </para>
/// <para>
/// <b>Recording never throws.</b> Opening and closing the record are each wrapped in <c>catch (Exception)</c> and the
/// failure is swallowed: a dialog is shown on the dispatcher thread, where an escaping exception is classified as
/// <c>UNHANDLED_UI_ERROR</c> and latches the vehicle (<c>App.OnDispatcherUnhandledException</c>). One of the dialogs
/// that goes through here is that latch's own explanation, so a failing record must not stop it from appearing either.
/// Nothing is logged from here: the logger itself can throw (research section 4.3, item 1), and a lost record only makes
/// the snapshot under-report a dialog.
/// </para>
/// <para>
/// <b>Only the title and the call site are recorded, never the body.</b> Some bodies carry a sublot (the fault-cargo
/// handoff and forced mechanical recovery confirmations open with <c>RecoveryTargetConfirmationText</c>) or text the
/// operator typed (the hardware recovery record's resend paragraph). Every title is a literal;
/// <c>ModalDialogRegistrationArchitectureTests</c> holds both rules.
/// </para>
/// </remarks>
public static class ModalDialogs
{
    /// <summary>What the screen-state snapshot reads; one per process, because a modal dialog is.</summary>
    internal static IModalDialogRegistry Registry { get; } = new ModalDialogRegistry();

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        [CallerMemberName] string callSite = "") =>
        Show(Registry, Present, Request(messageBoxText, caption, button, icon), callSite);

    public static MessageBoxResult Show(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult,
        [CallerMemberName] string callSite = "") =>
        Show(Registry, Present, Request(messageBoxText, caption, button, icon, defaultResult), callSite);

    /// <summary>The request each public overload hands on; its own method so a test can hold it to the arguments.</summary>
    internal static ModalDialogRequest Request(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult? defaultResult = null) =>
        new(messageBoxText, caption, button, icon, defaultResult);

    /// <summary>The recording around one dialog, with the registry and the presenter given (tests replace both).</summary>
    internal static MessageBoxResult Show(
        IModalDialogRegistry registry,
        Func<ModalDialogRequest, MessageBoxResult> presenter,
        ModalDialogRequest request,
        string callSite)
    {
        object? entry = null;
        try
        {
            entry = registry.Opened(request.Caption, callSite);
        }
        catch (Exception)
        {
            // See the class remarks: a record that cannot be kept is dropped, the dialog is still shown.
        }

        try
        {
            return presenter(request);
        }
        finally
        {
            try
            {
                registry.Closed(entry);
            }
            catch (Exception)
            {
                // Same as above. Rethrowing from here would also replace whatever the dialog itself threw.
            }
        }
    }

    /// <summary>The request handed to <see cref="MessageBox"/>, overload for overload.</summary>
    internal static MessageBoxResult Present(ModalDialogRequest request) =>
        request.DefaultResult is { } defaultResult
            ? MessageBox.Show(request.MessageBoxText, request.Caption, request.Button, request.Icon, defaultResult)
            : MessageBox.Show(request.MessageBoxText, request.Caption, request.Button, request.Icon);
}

/// <summary>The arguments of one <see cref="MessageBox"/> call; <see cref="DefaultResult"/> is null for the overload without it.</summary>
internal sealed record ModalDialogRequest(
    string MessageBoxText,
    string Caption,
    MessageBoxButton Button,
    MessageBoxImage Icon,
    MessageBoxResult? DefaultResult);

/// <summary>Which modal dialogs are open, innermost last.</summary>
internal interface IModalDialogRegistry
{
    /// <summary>Records a dialog about to be shown; returns the token <see cref="Closed"/> takes back.</summary>
    object? Opened(string title, string callSite);

    /// <summary>Removes the dialog <see cref="Opened"/> recorded. A null or unknown token is ignored.</summary>
    void Closed(object? entry);

    /// <summary>The open dialogs, outermost first.</summary>
    IReadOnlyList<OpenModalDialog> Read();
}

/// <summary>One open dialog: its title (a literal) and the method that showed it.</summary>
public sealed record OpenModalDialog(string Title, string CallSite);

/// <summary>
/// The process registry. Locked rather than dispatcher-bound: the startup failure notice can be shown before the main
/// window exists, from <c>StartupFailureNotice</c>, so not every caller is on the dispatcher thread.
/// </summary>
internal sealed class ModalDialogRegistry : IModalDialogRegistry
{
    private readonly object _sync = new();
    private readonly List<OpenModalDialog> _open = [];

    public object? Opened(string title, string callSite)
    {
        OpenModalDialog entry = new(title, callSite);
        lock (_sync)
        {
            _open.Add(entry);
        }

        return entry;
    }

    public void Closed(object? entry)
    {
        if (entry is not OpenModalDialog dialog)
        {
            return;
        }

        lock (_sync)
        {
            // By reference: two dialogs with the same title and call site are two entries.
            int index = _open.FindLastIndex(open => ReferenceEquals(open, dialog));
            if (index >= 0)
            {
                _open.RemoveAt(index);
            }
        }
    }

    public IReadOnlyList<OpenModalDialog> Read()
    {
        lock (_sync)
        {
            return [.. _open];
        }
    }
}
