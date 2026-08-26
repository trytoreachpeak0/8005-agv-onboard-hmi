using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class ConfigurationTests
{
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
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e",
                ServerCertificateSha256 = new string('a', 64)
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
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e",
                ServerCertificateSha256 = new string('a', 64)
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
        WireToGateSettings? wireToGate = null) =>
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
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e",
                ServerCertificateSha256 = new string('a', 64)
            },
            IoModule = new IoModuleSettings { Host = "io-module.internal" }
        };

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
