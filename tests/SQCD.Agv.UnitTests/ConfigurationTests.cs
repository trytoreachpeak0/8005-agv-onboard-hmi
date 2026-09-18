using System.Text.Json;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.json")]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.Production.example.json")]
    public void WireToGateExamplesUseTheAuthoritativeControlServerPort(string relativePath)
    {
        string path = FindRepositoryFile(relativePath);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        int configuredPort = document.RootElement
            .GetProperty("wireToGate")
            .GetProperty("port")
            .GetInt32();
        JsonElement wireToGate = document.RootElement.GetProperty("wireToGate");
        Uri vehicleSafetyEndpoint = new(document.RootElement
            .GetProperty("vehicleSafety")
            .GetProperty("endpoint")
            .GetString()!);

        Assert.Equal(WireToGateSettings.DefaultControlServerPort, configuredPort);
        Assert.Equal(58_005, configuredPort);
        Assert.False(wireToGate.TryGetProperty("useTls", out _));
        Assert.False(wireToGate.TryGetProperty("serverCertificateSha256", out _));
        Assert.Equal(Uri.UriSchemeHttp, vehicleSafetyEndpoint.Scheme);
    }

    [Fact]
    public void ProductionWithoutWireToGateIsRejected()
    {
        OnboardSettings settings = new() { Environment = "Production" };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("必须启用WIRE_TO_GATE", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsLoopbackControlServer()
    {
        using EnvironmentVariableScope credentials = new();
        OnboardSettings settings = CreateValidProductionSettings(
            wireToGate: new WireToGateSettings
            {
                Enabled = true,
                Host = "127.0.0.1",
                OnboardInstanceId = "77a9a4b8-7b1c-4f2b-92bd-3872f5871158",
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e"
            });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("ControlServer地址", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsExampleControlServerAddress()
    {
        using EnvironmentVariableScope credentials = new(clear: true);
        OnboardSettings settings = CreateValidProductionSettings(
            wireToGate: new WireToGateSettings
            {
                Enabled = true,
                Host = "control.example.internal",
                OnboardInstanceId = "77a9a4b8-7b1c-4f2b-92bd-3872f5871158",
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e"
            });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("ControlServer地址", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionRejectsMissingCredentialOrOperatorIdentity()
    {
        using EnvironmentVariableScope credentials = new(clear: true);
        OnboardSettings settings = CreateValidProductionSettings();

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("凭据", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionAcceptsCompleteNonPlaceholderConfiguration()
    {
        using EnvironmentVariableScope credentials = new();
        OnboardSettings settings = CreateValidProductionSettings();

        settings.Validate();
    }

    [Fact]
    public void EnabledVehicleSafetyProjectionAcceptsPlainHttp()
    {
        OnboardSettings settings = new()
        {
            VehicleSafety = new VehicleSafetySettings
            {
                Enabled = true,
                Endpoint = "http://control.internal/api/onboard/v1/vehicle-safety",
                ExpectedVehicleKey = "AGV-8005-27"
            }
        };

        settings.Validate();
    }

    [Fact]
    public void EnabledVehicleSafetyProjectionRejectsHttps()
    {
        OnboardSettings settings = new()
        {
            VehicleSafety = new VehicleSafetySettings
            {
                Enabled = true,
                Endpoint = "https://control.internal/api/onboard/v1/vehicle-safety",
                ExpectedVehicleKey = "AGV-8005-27"
            }
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("必须使用HTTP", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_001)]
    [InlineData(5_000)]
    public void EnabledVehicleSafetyProjectionRejectsUnsafeClockSkewTolerance(int toleranceMs)
    {
        OnboardSettings settings = new()
        {
            VehicleSafety = new VehicleSafetySettings
            {
                Enabled = true,
                Endpoint = "http://control.internal/api/onboard/v1/vehicle-safety",
                ExpectedVehicleKey = "AGV-8005-27",
                ClockSkewToleranceMs = toleranceMs
            }
        };

        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Theory]
    [InlineData("useTls")]
    [InlineData("serverCertificateSha256")]
    public void RemovedTransportKeyIsRejectedInsteadOfSilentlyIgnored(string removedKey)
    {
        string path = Path.Combine(Path.GetTempPath(), $"onboard-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""
                {
                  "wireToGate": {
                    "{{removedKey}}": false
                  }
                }
                """);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(() => OnboardSettings.Load(path));

            Assert.Contains("已移除", exception.Message, StringComparison.Ordinal);
            Assert.Contains("明文TCP/HTTP", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProductionRequiresEnabledVehicleSafetyProjection()
    {
        using EnvironmentVariableScope credentials = new();
        OnboardSettings settings = CreateValidProductionSettings(
            vehicleSafety: new VehicleSafetySettings { Enabled = false });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("必须启用ControlServer车辆安全投影", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 期待动作超时门槛（REQ-0358，onboard-hmi#109）：不配时是 3 个 <c>OperationTimeout</c>，跟着它走；投运按现场实测
    /// 标定时直接写毫秒数。
    /// </summary>
    [Fact]
    public void TheExpectedActionOverdueThresholdDefaultsToThreeOperationTimeoutsAndCanBeConfigured()
    {
        Assert.Equal(TimeSpan.FromMinutes(6), new WorkflowSettings().ExpectedActionOverdueThreshold);
        Assert.Equal(
            TimeSpan.FromSeconds(90),
            new WorkflowSettings { OperationTimeoutMs = 30_000 }.ExpectedActionOverdueThreshold);
        Assert.Equal(
            TimeSpan.FromMinutes(8),
            new WorkflowSettings { ExpectedActionOverdueMs = 480_000 }.ExpectedActionOverdueThreshold);

        string path = Path.Combine(Path.GetTempPath(), $"onboard-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "workflow": {
                    "expectedActionOverdueMs": 420000
                  }
                }
                """);

            Assert.Equal(
                TimeSpan.FromMinutes(7),
                OnboardSettings.Load(path).Workflow.ExpectedActionOverdueThreshold);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveExpectedActionOverdueThresholdIsRejected(int milliseconds)
    {
        OnboardSettings settings = new()
        {
            Workflow = new WorkflowSettings { ExpectedActionOverdueMs = milliseconds }
        };

        Assert.Throws<InvalidDataException>(settings.Validate);
    }

    [Fact]
    public void DuplicateUnlockOutputChannelIsRejected()
    {
        SlotIoMapping[] slots = CreateDefaultSlots();
        slots[1] = new SlotIoMapping
        {
            SlotIndex = 1,
            DoChannel = 0,
            LockFeedbackDiChannel = 1,
            LightCurtainDiChannel = 9
        };
        OnboardSettings settings = new()
        {
            IoModule = new IoModuleSettings { Slots = slots }
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("开锁DO通道不得重复", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InputChannelSharedByLockAndLightCurtainIsRejected()
    {
        SlotIoMapping[] slots = CreateDefaultSlots();
        slots[7] = new SlotIoMapping
        {
            SlotIndex = 7,
            DoChannel = 7,
            LockFeedbackDiChannel = 7,
            LightCurtainDiChannel = 0
        };
        OnboardSettings settings = new()
        {
            IoModule = new IoModuleSettings { Slots = slots }
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("16个互不重复", exception.Message, StringComparison.Ordinal);
    }

    private static SlotIoMapping[] CreateDefaultSlots() =>
        Enumerable.Range(0, 8)
            .Select(index => new SlotIoMapping
            {
                SlotIndex = index,
                DoChannel = (ushort)index,
                LockFeedbackDiChannel = (ushort)index,
                LightCurtainDiChannel = (ushort)(index + 8)
            })
            .ToArray();

    private static OnboardSettings CreateValidProductionSettings(
        WireToGateSettings? wireToGate = null,
        VehicleSafetySettings? vehicleSafety = null) =>
        new()
        {
            Environment = "Production",
            AgvId = "AGV-8005-27",
            OnboardInstanceId = "ONBOARD-8005-27",
            RuleGateway = new RuleGatewaySettings { Host = "legacy.disabled.internal" },
            WireToGate = wireToGate ?? new WireToGateSettings
            {
                Enabled = true,
                Host = "control.internal",
                OnboardInstanceId = "77a9a4b8-7b1c-4f2b-92bd-3872f5871158",
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e"
            },
            VehicleSafety = vehicleSafety ?? new VehicleSafetySettings
            {
                Enabled = true,
                Endpoint = "http://control.internal/api/onboard/v1/vehicle-safety",
                CredentialEnvironmentVariable = "CONTROL_SERVER_ONBOARD_CREDENTIAL",
                ExpectedVehicleKey = "AGV-8005-27"
            },
            IoModule = new IoModuleSettings { Host = "io-module.internal" }
        };

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("找不到测试所需的仓库配置文件。", relativePath);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string? _credential = Environment.GetEnvironmentVariable("CONTROL_SERVER_ONBOARD_CREDENTIAL");
        private readonly string? _operator = Environment.GetEnvironmentVariable("CONTROL_SERVER_OPERATOR_ID");

        public EnvironmentVariableScope(bool clear = false)
        {
            Environment.SetEnvironmentVariable(
                "CONTROL_SERVER_ONBOARD_CREDENTIAL",
                clear ? null : "test-credential");
            Environment.SetEnvironmentVariable(
                "CONTROL_SERVER_OPERATOR_ID",
                clear ? null : "test-operator");
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("CONTROL_SERVER_ONBOARD_CREDENTIAL", _credential);
            Environment.SetEnvironmentVariable("CONTROL_SERVER_OPERATOR_ID", _operator);
        }
    }
}
