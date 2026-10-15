using System.Collections;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using SQCD.Agv.Wpf.ScreenState;
using SQCD.Agv.Wpf.ViewModels;
using Xunit;

namespace SQCD.Agv.WireToGateG2Tests;

/// <summary>
/// The guard that the screen-state snapshot covers the screen (onboard-hmi#313, T1a): every <c>Button</c> and every
/// editable <c>TextBox</c> in <c>MainWindow.xaml</c>, and every element carrying a bound <c>ItemStatus</c>, appears in
/// <see cref="HmiScreenBindings"/> with the binding paths the XAML writes -- and nothing appears there that the XAML no
/// longer has.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Failure 2 of the research's section 6 ("漏项"): a button on the screen that the snapshot does not
/// report makes every reading of the snapshot wrong in a way nobody notices. A new button, input box or raw code added
/// to the window without a row in the table turns this red; so does a row whose binding the XAML has since changed,
/// because the builder reads the paths in the table, not the XAML.
/// </para>
/// <para>
/// <b>What this cannot see.</b> It reads the XAML text of <c>MainWindow.xaml</c> only: a binding set in a
/// <c>Style</c>/<c>Setter</c> or a trigger rather than on the element, a control created in code-behind, a control in
/// another window or a resource dictionary, a <c>TextBox</c> marked <c>IsReadOnly="True"</c> (counted as not
/// editable), and a <c>Visibility</c> or <c>IsEnabled</c> that a parent panel overrides (the snapshot reports the
/// control's own binding, as the research defines "visible"). It also assumes every <c>Visibility</c> binding goes
/// through <c>BooleanToVisibilityConverter</c>, and checks that it does; and that a button or input box never sets
/// <c>IsEnabled</c> or <c>Visibility</c> to a literal, which the builder would read as "no binding", that is, enabled
/// and visible (onboard-hmi#313 review L6).
/// </para>
/// <para>
/// <b>Left for T2/T3</b> (review L6): the list boxes' selection (the worklist and the recovery demand choices; the
/// snapshot reports the chosen recovery demand, not the worklist's selected row), and controls other than
/// <c>Button</c> and <c>TextBox</c> that take input.
/// </para>
/// </remarks>
public sealed class HmiScreenStateXamlCoverageArchitectureTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] StateAttributes = ["IsEnabled", "Visibility"];

    [Fact]
    public void EveryButtonInTheWindowIsInTheSnapshotWithItsBindings()
    {
        HmiScreenBindings.ButtonBinding[] fromXaml = [.. Window().Descendants(Presentation + "Button")
            .Where(button => !InWorklistRow(button))
            .Select(ButtonFromXaml)];

        Assert.Equal(fromXaml, HmiScreenBindings.Buttons);
    }

    /// <summary>
    /// The buttons in the worklist's row template are reported per row, their paths read off <see cref="WorklistItemRow"/>
    /// (8005-agv-onboard-hmi#353): a new one, or a changed binding, turns this red.
    /// </summary>
    [Fact]
    public void EveryButtonInAWorklistRowIsInTheRowStateWithItsBindings()
    {
        HmiScreenBindings.ButtonBinding[] fromXaml = [.. Window().Descendants(Presentation + "Button")
            .Where(InWorklistRow)
            .Select(ButtonFromXaml)];

        Assert.Equal(fromXaml, HmiScreenBindings.WorklistRowButtons);
        foreach (HmiScreenBindings.ButtonBinding button in HmiScreenBindings.WorklistRowButtons)
        {
            AssertPath(button.Visibility, typeof(bool), typeof(WorklistItemRow));
            AssertPath(button.IsEnabled, typeof(bool), typeof(WorklistItemRow));
        }
    }

    private static bool InWorklistRow(XElement button) =>
        button.Ancestors(Presentation + "ListBox")
            .Any(list => (string?)list.Attribute("AutomationProperties.AutomationId") == "WorklistItems");

    [Fact]
    public void EveryEditableInputBoxIsInTheSnapshotWithItsBindings()
    {
        HmiScreenBindings.InputBinding[] fromXaml = [.. Window().Descendants(Presentation + "TextBox")
            .Where(box => (string?)box.Attribute("IsReadOnly") != "True")
            .Select(InputFromXaml)];

        Assert.Equal(fromXaml, HmiScreenBindings.Inputs);
    }

    [Fact]
    public void EveryRawCodeOutsideATemplateIsInTheSnapshot()
    {
        HmiScreenBindings.RawCodeBinding[] fromXaml = [.. Window().Descendants()
            .Where(element => Path(element.Attribute("AutomationProperties.ItemStatus")) is not null)
            .Where(element => !element.Ancestors(Presentation + "DataTemplate").Any())
            .Select(element => new HmiScreenBindings.RawCodeBinding(
                (string)element.Attribute("AutomationProperties.AutomationId")!,
                Path(element.Attribute("AutomationProperties.ItemStatus"))!,
                NearestVisibility(element.AncestorsAndSelf())))];

        Assert.Equal(fromXaml, HmiScreenBindings.RawCodes);
    }

    /// <summary>
    /// Visible means "the Visibility binding is true" only through <c>BooleanToVisibilityConverter</c>; an inverting
    /// converter would make the snapshot say the opposite of the screen.
    /// </summary>
    [Fact]
    public void EveryVisibilityBindingTheSnapshotReadsGoesThroughTheBooleanConverter()
    {
        string[] offenders = [.. Window().Descendants()
            .Select(element => (string?)element.Attribute("Visibility"))
            .OfType<string>()
            .Where(value => value.StartsWith("{Binding", StringComparison.Ordinal))
            .Where(value => !value.Contains("Converter={StaticResource BooleanToVisibilityConverter}", StringComparison.Ordinal))];

        Assert.Empty(offenders);
    }

    /// <summary>A literal would read as "no binding" -- enabled and visible -- whatever it says.</summary>
    [Fact]
    public void NoButtonOrInputBoxSetsItsStateToALiteral()
    {
        string[] offenders = [.. Window().Descendants()
            .Where(element => element.Name == Presentation + "Button" || element.Name == Presentation + "TextBox")
            .SelectMany(element => StateAttributes
                .Select(name => element.Attribute(name))
                .OfType<XAttribute>()
                .Where(attribute => Path(attribute) is null)
                .Select(attribute => $"{element.Name.LocalName} {attribute}"))];

        Assert.Empty(offenders);
    }

    /// <summary>The builder evaluates these paths by reflection; each must resolve on the view model to the type it reads.</summary>
    [Fact]
    public void EveryPathResolvesOnTheViewModelToWhatTheBuilderReads()
    {
        foreach (HmiScreenBindings.ButtonBinding button in HmiScreenBindings.Buttons)
        {
            AssertPath(button.Visibility, typeof(bool));
            AssertPath(button.IsEnabled, typeof(bool));
            AssertPath(button.Command, typeof(ICommand));
            AssertPath(button.ItemsSource, typeof(IEnumerable));
        }

        foreach (HmiScreenBindings.InputBinding input in HmiScreenBindings.Inputs)
        {
            AssertPath(input.Visibility, typeof(bool));
            AssertPath(input.IsEnabled, typeof(bool));
            AssertPath(input.Text, typeof(string));
            AssertPath(input.KeyCommand, typeof(ICommand));
        }

        foreach (HmiScreenBindings.RawCodeBinding code in HmiScreenBindings.RawCodes)
        {
            AssertPath(code.ItemStatus, typeof(object));
            AssertPath(code.Visibility, typeof(bool));
        }
    }

    private static void AssertPath(string? path, Type expected, Type? source = null)
    {
        if (path is null)
        {
            return;
        }

        Type type = source ?? typeof(MainViewModel);
        foreach (string name in path.Split('.'))
        {
            PropertyInfo? property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.True(property is not null, $"{type.Name} has no public property {name} (path {path})");
            type = property!.PropertyType;
        }

        Assert.True(expected.IsAssignableFrom(type), $"{path} is {type.Name}, the builder reads it as {expected.Name}");
    }

    private static HmiScreenBindings.ButtonBinding ButtonFromXaml(XElement button)
    {
        string? command = Path(button.Attribute("Command"));
        string? click = (string?)button.Attribute("Click");
        string? isEnabled = Path(button.Attribute("IsEnabled"));
        string? automationId = Literal(button.Attribute("AutomationProperties.AutomationId"));
        string? content = Literal(button.Attribute("Content"));
        bool inTemplate = button.Ancestors(Presentation + "DataTemplate").Any();
        if (command is not null)
        {
            return new(command, HmiScreenBindings.ButtonKind.Command, automationId, content, Path(button.Attribute("Visibility")),
                isEnabled, command, null);
        }

        Assert.True(click is not null, $"a Button with neither Command nor Click: {button}");
        if (inTemplate && isEnabled is null)
        {
            return new(click!, HmiScreenBindings.ButtonKind.Template, automationId, content,
                NearestVisibility(button.AncestorsAndSelf()), null, null,
                Path(button.Ancestors(Presentation + "ItemsControl").First().Attribute("ItemsSource")));
        }

        return new(click!, HmiScreenBindings.ButtonKind.Click, automationId, content, Path(button.Attribute("Visibility")),
            isEnabled, null, null);
    }

    private static HmiScreenBindings.InputBinding InputFromXaml(XElement box) =>
        new(
            Literal(box.Attribute("AutomationProperties.AutomationId")) ?? (string)box.Attribute(Xaml + "Name")!,
            Path(box.Attribute("Visibility")),
            Path(box.Attribute("IsEnabled")),
            Path(box.Attribute("Text"))!,
            box.Element(Presentation + "TextBox.InputBindings")?.Elements(Presentation + "KeyBinding")
                .Select(binding => Path(binding.Attribute("Command")))
                .SingleOrDefault());

    private static string? NearestVisibility(IEnumerable<XElement> elements) =>
        elements.Select(element => Path(element.Attribute("Visibility"))).FirstOrDefault(path => path is not null);

    /// <summary>The path of a <c>{Binding X, ...}</c> attribute; null for a missing attribute or a literal.</summary>
    private static string? Path(XAttribute? attribute) =>
        attribute is not null && Regex.Match(attribute.Value, @"^\{Binding\s+([^,}\s]+)") is { Success: true } match
            ? match.Groups[1].Value
            : null;

    private static string? Literal(XAttribute? attribute) =>
        attribute is null || attribute.Value.StartsWith('{') ? null : attribute.Value;

    private static XElement Window() =>
        XElement.Load(System.IO.Path.Combine(RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml"));

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(System.IO.Path.Combine(directory.FullName, "SQCD_8005AGV.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("No SQCD_8005AGV.sln above " + AppContext.BaseDirectory);
    }
}
