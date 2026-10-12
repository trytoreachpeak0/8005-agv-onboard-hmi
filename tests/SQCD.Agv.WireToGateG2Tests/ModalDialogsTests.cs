using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using SQCD.Agv.Wpf;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// <c>ModalDialogs</c>, the record of open dialogs the screen-state snapshot reads (onboard-hmi#313, T1a): it passes
/// every argument through to <see cref="MessageBox"/> and its result back unchanged, keeps an accurate count, and
/// never lets a failure of its own reach the dispatcher.
/// </summary>
/// <remarks>
/// No real dialog is shown: the presenter is replaced. What is not covered here is the two-line
/// <c>ModalDialogs.Present</c> itself (choosing the <see cref="MessageBox"/> overload by whether a default result was
/// given), which only a real dialog could exercise; <see cref="EachOverloadHasTheSameParametersAsAMessageBoxOverload"/>
/// holds the signatures instead.
/// </remarks>
public sealed class ModalDialogsTests
{
    [Fact]
    public void EachOverloadHandsOnItsArgumentsUnchanged()
    {
        Assert.Equal(
            new ModalDialogRequest("正文", "标题", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No),
            ModalDialogs.Request("正文", "标题", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No));
        Assert.Equal(
            new ModalDialogRequest("正文", "标题", MessageBoxButton.OK, MessageBoxImage.Error, null),
            ModalDialogs.Request("正文", "标题", MessageBoxButton.OK, MessageBoxImage.Error));

        ModalDialogRequest request = ModalDialogs.Request("正文", "标题", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        ModalDialogRequest? presented = null;
        MessageBoxResult result = ModalDialogs.Show(
            new ModalDialogRegistry(),
            shown =>
            {
                presented = shown;
                return MessageBoxResult.Yes;
            },
            request,
            "OnTestClick");

        Assert.Same(request, presented);
        Assert.Equal(MessageBoxResult.Yes, result);
    }

    /// <summary>Apart from the trailing call site, each overload is one of <see cref="MessageBox"/>'s, parameter for parameter.</summary>
    [Fact]
    public void EachOverloadHasTheSameParametersAsAMessageBoxOverload()
    {
        MethodInfo[] overloads = [.. typeof(ModalDialogs).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == nameof(ModalDialogs.Show))];
        Assert.Equal(2, overloads.Length);
        foreach (MethodInfo overload in overloads)
        {
            ParameterInfo[] parameters = overload.GetParameters();
            Assert.Equal("callSite", parameters[^1].Name);
            Type[] types = [.. parameters[..^1].Select(parameter => parameter.ParameterType)];
            Assert.NotNull(typeof(MessageBox).GetMethod(nameof(MessageBox.Show), types));
            Assert.Equal(typeof(MessageBoxResult), overload.ReturnType);
        }
    }

    [Fact]
    public void ADialogIsRecordedWhileItIsOpenAndNestedDialogsCount()
    {
        ModalDialogRegistry registry = new();
        (int Depth, OpenModalDialog? Top)[] seen = new (int, OpenModalDialog?)[2];

        ModalDialogs.Show(
            registry,
            _ =>
            {
                seen[0] = (registry.Read().Count, registry.Read()[^1]);
                ModalDialogs.Show(
                    registry,
                    _ =>
                    {
                        seen[1] = (registry.Read().Count, registry.Read()[^1]);
                        return MessageBoxResult.OK;
                    },
                    ModalDialogs.Request("里层", "无法取消操作", MessageBoxButton.OK, MessageBoxImage.Information),
                    "OnCancelOperationClick");
                return MessageBoxResult.Yes;
            },
            ModalDialogs.Request("外层", "取消本次操作", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No),
            "OnCancelOperationClick");

        Assert.Equal((1, new OpenModalDialog("取消本次操作", "OnCancelOperationClick")), seen[0]);
        Assert.Equal((2, new OpenModalDialog("无法取消操作", "OnCancelOperationClick")), seen[1]);
        Assert.Empty(registry.Read());
    }

    /// <summary>A dialog that throws is transparent too: the same exception, and the record is still closed.</summary>
    [Fact]
    public void ADialogThatThrowsLeavesNoRecordAndThrowsItsOwnException()
    {
        ModalDialogRegistry registry = new();
        InvalidOperationException thrown = new("dialog");

        InvalidOperationException caught = Assert.Throws<InvalidOperationException>(() => ModalDialogs.Show(
            registry,
            _ => throw thrown,
            ModalDialogs.Request("正文", "标题", MessageBoxButton.OK, MessageBoxImage.Error),
            "OnDispatcherUnhandledException"));

        Assert.Same(thrown, caught);
        Assert.Empty(registry.Read());
    }

    /// <summary>
    /// Research section 4.3 items 1 and 5 for the one piece of T1a that runs in the product: on a real dispatcher, a
    /// registry that throws on both open and close does not reach <see cref="Dispatcher.UnhandledException"/> (which the
    /// application turns into <c>UNHANDLED_UI_ERROR</c> and a latched vehicle), the dialog is still shown, and its result
    /// still comes back. <c>App.OnDispatcherUnhandledException</c> cannot be reached from a test, so the assertion sits on
    /// the dispatcher.
    /// </summary>
    [Fact]
    public void ARegistryThatThrowsNeverReachesTheDispatcher()
    {
        int unhandled = 0;
        int presented = 0;
        MessageBoxResult? result = null;
        Exception? failure = null;
        Thread thread = new(() =>
        {
            try
            {
                Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.UnhandledException += (_, args) =>
                {
                    Interlocked.Increment(ref unhandled);
                    args.Handled = true;
                };
                dispatcher.BeginInvoke(() =>
                {
                    result = ModalDialogs.Show(
                        new ThrowingRegistry(),
                        _ =>
                        {
                            presented++;
                            return MessageBoxResult.Yes;
                        },
                        ModalDialogs.Request("正文", "软件运行异常", MessageBoxButton.OK, MessageBoxImage.Error),
                        "OnDispatcherUnhandledException");
                });
                dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => dispatcher.InvokeShutdown());
                Dispatcher.Run();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "the dispatcher did not shut down");
        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        Assert.Equal(0, unhandled);
        Assert.Equal(1, presented);
        Assert.Equal(MessageBoxResult.Yes, result);
    }

    /// <summary>A registry whose every member throws a non-IO exception, the kind no logger here catches.</summary>
    private sealed class ThrowingRegistry : IModalDialogRegistry
    {
        public object? Opened(string title, string callSite) => throw new InvalidOperationException("opened");

        public void Closed(object? entry) => throw new InvalidOperationException("closed");

        public IReadOnlyList<OpenModalDialog> Read() => throw new InvalidOperationException("read");
    }
}
