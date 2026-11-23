using System.Xml.Linq;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 车载屏上的字一律整串显示，不截成省略号（8005-agv-onboard-hmi#426）。v7 现场本站清单里的子批号末尾变成「…」：
/// 那一格设了 <c>TextTrimming="CharacterEllipsis"</c>，全串只在 ToolTip 里，而车载屏是触屏，ToolTip 看不到。
/// 放不下的字要换行或改排版，不能截掉。
/// </summary>
/// <remarks>
/// 扫 <c>src</c> 下每一个 <c>.xaml</c>：元素上的 <c>TextTrimming</c> 属性，和样式里 <c>Property="TextTrimming"</c> 的 setter，
/// 值只准是 <c>None</c>。读的是源码文本，所以以后新加的窗口、对话框也在管内。
/// </remarks>
public sealed class NoTextTrimmingXamlTests
{
    [Fact]
    public void NoXamlInTheProductTrimsText()
    {
        string root = ProtocolIdentityArchitectureTests.RepositoryRoot();
        string[] files = Directory.GetFiles(Path.Combine(root, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .ToArray();
        Assert.Contains(files, path => path.EndsWith(Path.Combine("SQCD.Agv.Wpf", "MainWindow.xaml"), StringComparison.Ordinal));

        string[] trimming =
        [
            .. files.SelectMany(path => Trimming(path).Select(found => $"{Path.GetRelativePath(root, path)}:{found}"))
        ];

        Assert.Empty(trimming);
    }

    private static IEnumerable<string> Trimming(string path)
    {
        XElement document = XElement.Load(path, LoadOptions.SetLineInfo);
        foreach (XElement element in document.DescendantsAndSelf())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                bool isTrimmingAttribute = attribute.Name.LocalName == "TextTrimming"
                    || attribute.Name.LocalName.EndsWith(".TextTrimming", StringComparison.Ordinal);
                if (isTrimmingAttribute && !IsNone(attribute.Value))
                {
                    yield return $"{Line(element)} {element.Name.LocalName} {attribute.Name.LocalName}=\"{attribute.Value}\"";
                }
            }

            if (element.Name.LocalName == "Setter"
                && ((string?)element.Attribute("Property"))?.Trim() is { } property
                && (property == "TextTrimming" || property.EndsWith(".TextTrimming", StringComparison.Ordinal)))
            {
                string value = (string?)element.Attribute("Value")
                    ?? element.Elements().FirstOrDefault()?.Value
                    ?? string.Empty;
                if (!IsNone(value))
                {
                    yield return $"{Line(element)} Setter {property}=\"{value}\"";
                }
            }
        }
    }

    private static bool IsNone(string value) => string.Equals(value.Trim(), "None", StringComparison.Ordinal);

    private static int Line(XElement element) => ((System.Xml.IXmlLineInfo)element).LineNumber;
}
