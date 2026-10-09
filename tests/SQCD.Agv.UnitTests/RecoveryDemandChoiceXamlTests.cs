using System.Xml.Linq;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 「车上待交接的需求」列表在 <c>MainWindow.xaml</c> 里的接线（8005-agv-onboard-hmi#209）：行要让 UIA 读得到名称与需求号，两个按钮
/// 要按「按得动」而不是「入口在」启用。
/// </summary>
/// <remarks>
/// UIA 读的是行容器 <c>ListBoxItem</c>：行模板的根元素没有自动化对象，标在那里读不到，所以名称与状态由 <c>ItemContainerStyle</c>
/// 的两个 setter 给。这一条只有真 WPF 看得见，G2 读的是视图模型，所以在这里钉住。
/// </remarks>
public sealed class RecoveryDemandChoiceXamlTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EachRowCarriesItsNameAndDemandForAutomation()
    {
        XElement list = Window().Descendants(Presentation + "ListBox")
            .Single(element => (string?)element.Attribute("AutomationProperties.AutomationId") == "RecoveryDemandChoices");

        Assert.Equal("{Binding RecoveryDemandChoices}", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("{Binding SelectedRecoveryDemandChoice, Mode=TwoWay}", (string?)list.Attribute("SelectedItem"));
        XElement style = list.Element(Presentation + "ListBox.ItemContainerStyle")!.Element(Presentation + "Style")!;
        Assert.Equal("ListBoxItem", (string?)style.Attribute("TargetType"));
        Dictionary<string, string> setters = style.Elements(Presentation + "Setter")
            .ToDictionary(setter => (string)setter.Attribute("Property")!, setter => (string)setter.Attribute("Value")!);
        Assert.Equal("{Binding Text}", setters["AutomationProperties.Name"]);
        Assert.Equal("{Binding DemandId}", setters["AutomationProperties.ItemStatus"]);
    }

    [Theory]
    [InlineData("OnFaultCargoHandoffClick", "CanPressFaultCargoHandoff", "CanRequestFaultCargoHandoff")]
    [InlineData("OnForcedMechanicalRecoveryClick", "CanPressForcedMechanicalRecovery", "CanRequestForcedMechanicalRecovery")]
    public void TheButtonIsEnabledOnlyWhenItCanBePressed(string click, string enabled, string visible)
    {
        XElement button = Window().Descendants(Presentation + "Button")
            .Single(element => (string?)element.Attribute("Click") == click);

        Assert.Equal($"{{Binding {enabled}}}", (string?)button.Attribute("IsEnabled"));
        Assert.StartsWith($"{{Binding {visible},", (string?)button.Attribute("Visibility"), StringComparison.Ordinal);
    }

    private static XElement Window() =>
        XElement.Load(Path.Combine(
            ProtocolIdentityArchitectureTests.RepositoryRoot(), "src", "SQCD.Agv.Wpf", "MainWindow.xaml"));
}
