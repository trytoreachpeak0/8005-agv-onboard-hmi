using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 让「车载端 G2 对某个切片说了话」这件事有机器守卫。
///
/// <c>ONBOARD_HMI_G2</c> 以前把整仓一次 build/test/format 的**同一个结论**盖到硬编码的
/// <c>W2G-IS-00</c> 与 <c>W2G-IS-01</c> 上，<c>IS-02..07</c> 一个字都没说。现在
/// <c>run-w2g-g2.ps1</c> 按 <c>--filter "IntegrationSlice=W2G-IS-0N"</c> 逐切片跑，
/// 每个切片拿自己那批测试的结论——**而这只有在标注本身可信时才成立**。本类守的就是标注：
///
/// 1. 切片清单从 vendored 的 <c>integration-slices/index.json</c> 读，测试里不出现第二份手抄清单；
/// 2. 那份副本按 <see cref="ApprovedIndexSha256"/> 钉字节，改了内容而不同步哈希立刻红；
/// 3. 标注出现的切片集合与清单**双向**相等——写错一个 id 会红，漏标一个切片也会红；
/// 4. 每个切片至少 <see cref="MinimumTestsPerSlice"/> 条，防止某个切片退化成象征性的一条。
///
/// 第 3 条的双向是关键：只查「标注 ⊆ 清单」的话，把 <c>IS-04</c> 的标注全删掉证据会照样绿，
/// 而那正是本轮要修的毛病。
///
/// 本类自己不挂任何 <c>IntegrationSlice</c> trait——它是横切守卫，不属于任何切片。
/// </summary>
public sealed class IntegrationSliceCoverageArchitectureTests
{
    private const string IndexRelativePath =
        "vendor/8005-agv-protocol/protocol-v0.3.0/integration-slices/index.json";

    /// <summary>
    /// <c>protocol-v0.3.0</c> 那份 <c>integration-slices/index.json</c> 的字节哈希。
    /// vendored 副本是协议仓 tag 的逐字节拷贝；这个常量让「悄悄改副本来让门禁变绿」失效。
    /// 协议升版时同时换路径、换文件、换这个值。
    /// </summary>
    private const string ApprovedIndexSha256 =
        "5fe92822ef0d7f881a8ea93dd1a28c997da03a16b4cabefe9986cb503fe8b9f9";

    private const int MinimumTestsPerSlice = 3;

    private static readonly Regex SliceTraitRegex = new(
        @"^\s*\[Trait\(\s*""IntegrationSlice""\s*,\s*""(?<slice>[^""]+)""\s*\)\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TestMethodRegex = new(
        @"^\s*(?:public|private|internal|protected)\b[^;]*?\b(?:Task|void|ValueTask)\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void VendoredSliceIndexStillMatchesTheApprovedProtocolRelease()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            IndexRelativePath.Replace('/', Path.DirectorySeparatorChar));

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Equal(ApprovedIndexSha256, actual);
    }

    [Fact]
    public void EverySliceInTheReleaseIsClaimedByAtLeastOneNamedTest()
    {
        string[] declared = ReadDeclaredSlices();
        Dictionary<string, IReadOnlyList<string>> annotated = ReadAnnotatedTests();

        string[] unclaimed = declared.Where(s => !annotated.ContainsKey(s)).ToArray();

        Assert.True(
            unclaimed.Length == 0,
            $"这些切片在 {IndexRelativePath} 里，却没有任何车载端测试认领，"
                + $"G2 无法为它们出结论：{string.Join(", ", unclaimed)}");
    }

    [Fact]
    public void NoTestClaimsASliceThatTheReleaseDoesNotDeclare()
    {
        string[] declared = ReadDeclaredSlices();
        Dictionary<string, IReadOnlyList<string>> annotated = ReadAnnotatedTests();

        var stray = annotated
            .Where(pair => !declared.Contains(pair.Key))
            .Select(pair => $"{pair.Key} <- {string.Join(", ", pair.Value)}")
            .ToArray();

        Assert.True(
            stray.Length == 0,
            $"这些标注指向 {IndexRelativePath} 里不存在的切片："
                + Environment.NewLine
                + string.Join(Environment.NewLine, stray));
    }

    [Fact]
    public void EverySliceCarriesEnoughTestsToBeMoreThanSymbolic()
    {
        string[] declared = ReadDeclaredSlices();
        Dictionary<string, IReadOnlyList<string>> annotated = ReadAnnotatedTests();

        var thin = declared
            .Where(s => annotated.TryGetValue(s, out IReadOnlyList<string>? tests)
                && tests.Count < MinimumTestsPerSlice)
            .Select(s => $"{s}: {annotated[s].Count} 条（下限 {MinimumTestsPerSlice}）")
            .ToArray();

        Assert.True(
            thin.Length == 0,
            "这些切片的车载端覆盖已经薄到不足以支撑一个 G2 结论："
                + Environment.NewLine
                + string.Join(Environment.NewLine, thin));
    }

    private static string[] ReadDeclaredSlices()
    {
        string path = Path.Combine(
            FindRepositoryRoot(),
            IndexRelativePath.Replace('/', Path.DirectorySeparatorChar));

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));

        return document.RootElement
            .GetProperty("slices")
            .EnumerateArray()
            .Select(slice => slice.GetProperty("integrationSliceId").GetString()!)
            .ToArray();
    }

    /// <summary>
    /// 扫源码而不是反射：两个测试程序集彼此不引用，反射只能看见自己这一个。
    /// 状态机按行走——属性块与方法签名之间不会有空行，空行即清空累积。
    /// </summary>
    private static Dictionary<string, IReadOnlyList<string>> ReadAnnotatedTests()
    {
        string root = FindRepositoryRoot();
        Dictionary<string, List<string>> bySlice = [];

        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal)
                || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            List<string> pending = [];
            foreach (string line in File.ReadLines(file, Encoding.UTF8))
            {
                Match trait = SliceTraitRegex.Match(line);
                if (trait.Success)
                {
                    pending.Add(trait.Groups["slice"].Value);
                    continue;
                }

                if (pending.Count > 0)
                {
                    Match method = TestMethodRegex.Match(line);
                    if (method.Success)
                    {
                        string name = $"{Path.GetFileName(file)}::{method.Groups["name"].Value}";
                        foreach (string slice in pending)
                        {
                            if (!bySlice.TryGetValue(slice, out List<string>? tests))
                            {
                                tests = [];
                                bySlice[slice] = tests;
                            }

                            tests.Add(name);
                        }

                        pending.Clear();
                    }
                    else if (line.Trim().Length == 0)
                    {
                        pending.Clear();
                    }
                }
            }
        }

        return bySlice.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.AsReadOnly());
    }

    private static string FindRepositoryRoot()
    {
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(Path.GetFullPath(start));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "SQCD_8005AGV.sln"))
                    && File.Exists(Path.Combine(
                        directory.FullName,
                        IndexRelativePath.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root from the test process directories.");
    }
}
