using SQCD.Agv.Application;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// <see cref="HmiScreenStateBuilder"/> on table rows the window does not have today (onboard-hmi#313, review M2): a
/// <c>Click</c> button with no <c>IsEnabled</c> binding is the very case research section 5 names, and the coverage guard
/// lets such a row in, so the builder has to read it rather than fail on a null path.
/// </summary>
public sealed class HmiScreenStateBuilderTests
{
    [Fact]
    public async Task AClickButtonWithoutAnIsEnabledBindingIsReportedEnabled()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);

        HmiButtonState state = HmiScreenStateBuilder.Button(
            viewModel,
            new HmiScreenBindings.ButtonBinding(
                "OnSomeClick", HmiScreenBindings.ButtonKind.Click, null, "某按钮", "HasWarning", null, null, null));

        Assert.True(state.Enabled);
        Assert.Equal(viewModel.HasWarning, state.Visible);
        Assert.Null(state.IsEnabledBinding);
    }

    [Fact]
    public async Task AClickButtonWithNeitherBindingIsReportedVisibleAndEnabled()
    {
        await using OnboardController controller = MultiDemandViewModelTests.Controller();
        MainViewModel viewModel = await MultiDemandViewModelTests.ViewModel(controller);

        HmiButtonState state = HmiScreenStateBuilder.Button(
            viewModel,
            new HmiScreenBindings.ButtonBinding(
                "OnSomeClick", HmiScreenBindings.ButtonKind.Click, null, "某按钮", null, null, null, null));

        Assert.Equal((true, true), (state.Visible, state.Enabled));
    }
}
