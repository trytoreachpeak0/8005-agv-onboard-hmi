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

    /// <summary>
    /// 两份出厂配置都把会话心跳写成 ADR-cross-0027 的 2 秒（onboard-hmi#142）。
    /// </summary>
    /// <remarks>
    /// 现场不改配置就该符合 ADR，所以默认值在代码与出厂文件里各写一遍，这条把两边钉在一起：
    /// 服务端按 6 秒静默判失联，出厂文件里留着 5 秒就是每次心跳都擦着阈值走。
    /// 顺带盯住 <c>ruleGateway.heartbeatIntervalMs</c>——那是旧规则网关的心跳，与 WIRE_TO_GATE
    /// 会话无关，本票不动它，两者写在同一份文件里，容易被一起改错。
    /// </remarks>
    /// <summary>
    /// 现场确认充不上入口出厂为关：两份出厂配置都写 <c>false</c>，代码默认值也是 <c>false</c>（8005-agv-onboard-hmi#222 审查第 1 条）。
    /// </summary>
    /// <remarks>
    /// 8005-agv-control-server#410 之前的服务端收到这条消息会关掉连接，协议里又没有服务端声明自己支持哪些消息的字段，
    /// 所以只能由现场在服务端支持之后打开。出厂文件里少了这一项，读出来的仍是代码默认值，所以两边一起钉。
    /// </remarks>
    [Theory]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.json")]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.Production.example.json")]
    public void TheUnableToChargeEntryShipsSwitchedOff(string relativePath)
    {
        string path = FindRepositoryFile(relativePath);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        JsonElement wireToGate = document.RootElement.GetProperty("wireToGate");

        Assert.True(
            wireToGate.TryGetProperty("unableToChargeEntryEnabled", out JsonElement configured),
            $"{relativePath} 的 wireToGate 节没有 unableToChargeEntryEnabled。");
        Assert.Equal(JsonValueKind.False, configured.ValueKind);
        Assert.False(new WireToGateSettings().UnableToChargeEntryEnabled);
    }

    /// <summary>
    /// 8005-agv-control-server#600：服务端凭车认投影，生产模板里出发安全投影的凭据变量与 WIRE_TO_GATE 的共享凭据分开，
    /// 地址带上这台车的 vehicleKey 供服务端核对。部署脚本按车把凭据写进这个变量。
    /// </summary>
    [Fact]
    public void TheProductionTemplateGivesTheSafetyProjectionAPerVehicleCredentialAndNamesTheVehicle()
    {
        string path = FindRepositoryFile("src/SQCD.Agv.Wpf/appsettings.Production.example.json");
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        JsonElement vehicleSafety = document.RootElement.GetProperty("vehicleSafety");
        string safetyVariable = vehicleSafety.GetProperty("credentialEnvironmentVariable").GetString()!;
        string sessionVariable = document.RootElement
            .GetProperty("wireToGate")
            .GetProperty("credentialEnvironmentVariable")
            .GetString()!;

        Assert.Equal("CONTROL_SERVER_ONBOARD_SAFETY_CREDENTIAL", safetyVariable);
        Assert.NotEqual(sessionVariable, safetyVariable);
        Assert.Equal(
            "http://REPLACE_CONTROL_SERVER_HOST/api/onboard/v1/vehicle-safety?vehicleKey=REPLACE_WITH_EXPECTED_VEHICLE_KEY",
            vehicleSafety.GetProperty("endpoint").GetString());
        Assert.Equal(
            "REPLACE_WITH_EXPECTED_VEHICLE_KEY",
            vehicleSafety.GetProperty("expectedVehicleKey").GetString());
    }

    [Theory]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.json")]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.Production.example.json")]
    public void WireToGateExamplesConfigureTheAdrSessionHeartbeatInterval(string relativePath)
    {
        string path = FindRepositoryFile(relativePath);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path),
            new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });

        JsonElement wireToGate = document.RootElement.GetProperty("wireToGate");

        Assert.True(
            wireToGate.TryGetProperty("sessionHeartbeatIntervalMs", out JsonElement configured),
            $"{relativePath} 的 wireToGate 节没有 sessionHeartbeatIntervalMs。");
        Assert.Equal(2_000, configured.GetInt32());
        // 消息超时是两条心跳到达间距的上界，与心跳间隔受同一道界约束（审查 S2）：出厂值必须严格
        // 小于它，3000 那个旧值恰好等于界、余量为零。
        Assert.Equal(2_500, wireToGate.GetProperty("messageTimeoutMs").GetInt32());
        Assert.True(
            wireToGate.GetProperty("messageTimeoutMs").GetInt32()
                < WireToGateSessionService.MaximumHeartbeatInterval.TotalMilliseconds,
            "出厂 messageTimeoutMs 必须严格小于 ADR-cross-0027 静默失联阈值的一半。");
        Assert.Equal(
            5_000,
            document.RootElement.GetProperty("ruleGateway").GetProperty("heartbeatIntervalMs").GetInt32());
    }

    /// <summary>
    /// 代码里的默认值就是 ADR-cross-0027 的三个数：2 秒心跳、6 秒静默失联、间隔上限取阈值的一半。
    /// </summary>
    /// <remarks>
    /// 上限是推出来的，不是拍的：丢一条心跳之后下一条要在阈值用完之前到，所以间隔必须严格小于
    /// 阈值的一半。把这三个数钉在一起，将来谁改了其中一个，改得对不对当场看得见。
    /// </remarks>
    [Fact]
    public void TheSessionHeartbeatDefaultsAreTheAdrNumbers()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), WireToGateSessionService.DefaultHeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(6), WireToGateSessionService.LivenessTimeout);
        Assert.Equal(TimeSpan.FromSeconds(3), WireToGateSessionService.MaximumHeartbeatInterval);
        Assert.Equal(2_000, new WireToGateSettings().SessionHeartbeatIntervalMs);
    }

    /// <summary>
    /// 0、负数、以及大到会撞上失联阈值的间隔，启动校验就拒掉，不带着走到现场。
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3_000)]
    [InlineData(int.MaxValue)]
    public void ASessionHeartbeatIntervalOutsideTheAdrBoundsIsRejected(int milliseconds)
    {
        OnboardSettings settings = CreateValidProductionSettings(
            wireToGate: new WireToGateSettings
            {
                Enabled = true,
                Host = "control.internal",
                OnboardInstanceId = "77a9a4b8-7b1c-4f2b-92bd-3872f5871158",
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e",
                SessionHeartbeatIntervalMs = milliseconds
            });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("会话心跳间隔", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// 消息超时也受心跳那道界约束：它就是两条心跳到达间距的上界（onboard-hmi#142 审查 S2）。
    /// </summary>
    /// <remarks>
    /// 心跳循环串行地等 <c>HeartbeatAck</c>，服务端看到的到达间距是 max(心跳间隔, ack 往返)，
    /// 而 ack 往返的上界就是这个超时。只卡心跳间隔不卡它，那道界就能从另一扇门绕开：间隔配 2 秒、
    /// 超时配 10 秒，服务端慢应答时两条心跳隔 10 秒才到，ADR-cross-0027 的「单次丢失不构成失联」
    /// 当场失效。出厂值同日由 3000 下调到 2500——3000 恰好等于那道界，保证在但余量为零。
    /// </remarks>
    [Theory]
    [InlineData(3_000)]
    [InlineData(10_000)]
    [InlineData(int.MaxValue)]
    public void AMessageTimeoutThatWouldStretchTheHeartbeatGapIsRejected(int milliseconds)
    {
        OnboardSettings settings = CreateValidProductionSettings(
            wireToGate: new WireToGateSettings
            {
                Enabled = true,
                Host = "control.internal",
                OnboardInstanceId = "77a9a4b8-7b1c-4f2b-92bd-3872f5871158",
                OnboardBuildCommit = "a6f05fbced15316a2cc20cd327f80c5c5ee1821e",
                MessageTimeoutMs = milliseconds
            });

        InvalidDataException exception = Assert.Throws<InvalidDataException>(settings.Validate);

        Assert.Contains("消息超时", exception.Message, StringComparison.Ordinal);
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

    /// <summary>
    /// A vehicle's existing settings file still carries <c>wireToGate.supportsBatchUnlock</c>, which protocol
    /// 3.0.0 removed from <c>CapabilitySnapshot</c> (8005-agv-onboard-hmi#214). The key is ignored, not
    /// refused: a refused settings file stops the onboard program silently before its logger exists, so on
    /// site the vehicle would simply not start over a key that no longer means anything.
    /// </summary>
    [Fact]
    public void ALeftoverBatchUnlockKeyInAnOldSettingsFileIsIgnored()
    {
        string json = File.ReadAllText(FindRepositoryFile("src/SQCD.Agv.Wpf/appsettings.json"));
        int wireToGate = json.IndexOf("\"wireToGate\": {", StringComparison.Ordinal);
        Assert.True(wireToGate >= 0, "appsettings.json has no wireToGate section to add the leftover key to");
        int brace = json.IndexOf('{', wireToGate);
        string withLeftover = json.Insert(brace + 1, "\n    \"supportsBatchUnlock\": true,");
        string path = Path.Combine(Path.GetTempPath(), $"onboard-settings-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, withLeftover);

            OnboardSettings settings = OnboardSettings.Load(path);

            Assert.Contains("supportsBatchUnlock", File.ReadAllText(path), StringComparison.Ordinal);
            // Loaded as the unmodified file loads: same section, same values.
            Assert.Equal(OnboardSettings.Load(FindRepositoryFile("src/SQCD.Agv.Wpf/appsettings.json")).WireToGate.Port, settings.WireToGate.Port);
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

    /// <summary>
    /// 屏幕状态快照出厂为关，所有环境都是，Production 也是（onboard-hmi#315，用户 2026-10-12 定的两步走）。
    /// </summary>
    /// <remarks>
    /// 先只在 agv02、agv03 的现场配置里开，跑满一个现场周期再另开票翻 Production 默认值。代码默认值与两份出厂文件
    /// 一起钉：出厂文件里少了这一项，读出来的仍是代码默认值。翻默认值的那张票要改的正是这一条。
    /// </remarks>
    [Theory]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.json")]
    [InlineData("src/SQCD.Agv.Wpf/appsettings.Production.example.json")]
    public void TheScreenStateSnapshotShipsSwitchedOff(string relativePath)
    {
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(FindRepositoryFile(relativePath)),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });

        Assert.False(new ScreenStateSettings().Enabled);
        Assert.False(document.RootElement.GetProperty("screenState").GetProperty("enabled").GetBoolean());
    }

    /// <summary>
    /// Production 下快照默认关，配置可开：开着的 Production 配置照常通过校验，不像自动化接口那样被禁止。
    /// </summary>
    [Fact]
    public void ProductionLeavesTheScreenStateOffByDefaultAndAcceptsItSwitchedOn()
    {
        using EnvironmentVariableScope _ = new();
        OnboardSettings byDefault = CreateValidProductionSettings();
        byDefault.Validate();
        Assert.False(byDefault.ScreenState.Enabled);

        OnboardSettings switchedOn = new()
        {
            Environment = byDefault.Environment,
            AgvId = byDefault.AgvId,
            OnboardInstanceId = byDefault.OnboardInstanceId,
            RuleGateway = byDefault.RuleGateway,
            WireToGate = byDefault.WireToGate,
            VehicleSafety = byDefault.VehicleSafety,
            IoModule = byDefault.IoModule,
            ScreenState = new ScreenStateSettings { Enabled = true }
        };
        switchedOn.Validate();
        Assert.True(switchedOn.ScreenState.Enabled);
    }

    /// <summary>
    /// 快照参数写错从不挡住车载端启动（onboard-hmi#315 审查 S4）：开着时整体校验照样通过，问题由 <c>Problem()</c> 报出，
    /// 生效配置摘要写明本次不启动；接线据此不启动快照（<c>HmiScreenStateWiringTests</c>）。
    /// </summary>
    [Theory]
    [InlineData(10, 5_000, 20_971_520, 30)]
    [InlineData(500, 500, 20_971_520, 30)]
    [InlineData(500, 120_000, 20_971_520, 30)]
    [InlineData(500, 5_000, 1_024, 30)]
    [InlineData(500, 5_000, 20_971_520, 0)]
    public void InvalidScreenStateParametersNeverStopTheHmiFromStarting(
        int flushIntervalMs,
        int heartbeatIntervalMs,
        long timelineMaxFileBytes,
        int timelineRetentionDays)
    {
        OnboardSettings Settings(bool enabled) => new()
        {
            ScreenState = new ScreenStateSettings
            {
                Enabled = enabled,
                FlushIntervalMs = flushIntervalMs,
                HeartbeatIntervalMs = heartbeatIntervalMs,
                TimelineMaxFileBytes = timelineMaxFileBytes,
                TimelineRetentionDays = timelineRetentionDays
            }
        };

        Settings(enabled: false).Validate();
        Settings(enabled: true).Validate();
        Assert.NotNull(Settings(enabled: true).ScreenState.Problem());
        Assert.Contains("screenState.enabled=True（参数无效，本次不启动）", Settings(enabled: true).DescribeForTechnicalLog(), StringComparison.Ordinal);
        Assert.Contains("screenState.enabled=False。", Settings(enabled: false).DescribeForTechnicalLog(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheShippedScreenStateParametersAreValid()
    {
        Assert.Null(new ScreenStateSettings { Enabled = true }.Problem());
        Assert.Contains("screenState.enabled=True。", new OnboardSettings { ScreenState = new ScreenStateSettings { Enabled = true } }.DescribeForTechnicalLog(), StringComparison.Ordinal);
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
