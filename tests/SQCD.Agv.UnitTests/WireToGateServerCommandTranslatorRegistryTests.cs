using System.Text.Json;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

/// <summary>
/// 新增一条服务端消息的处理只加文件，不改 <c>WireToGateSessionClient.cs</c>。
/// </summary>
/// <remarks>
/// 本类不挂 <c>IntegrationSlice</c> trait：它守的是消息处理的注册方式，不属于
/// <c>W2G-IS-00</c>～<c>07</c> 里的任何一个切片；而 <c>FP-IS-14</c> 还不在 vendored 的
/// <c>integration-slices/index.json</c> 里，标上去会让
/// <see cref="IntegrationSliceCoverageArchitectureTests"/> 的双向相等直接红。
/// </remarks>
public sealed class WireToGateServerCommandTranslatorRegistryTests
{
    [Fact]
    public void ANewMessageIsHandledByAddingATranslatorAndNothingElse()
    {
        // 这个翻译器整个定义在本文件里——没有碰 WireToGateSessionClient.cs 一个字节，
        // 也没有碰任何既有共享文件。注册表认它，就是「只加文件」这条约定成立的证据。
        WireToGateServerCommandTranslatorRegistry registry = new([new ExampleTranslator()]);

        Assert.True(registry.TryTranslate(
            Envelope("ExampleNewMessage", """{"exampleId":"E-1"}"""),
            out WireToGateServerCommand? command));
        WireToGateRecoveryCommand translated = Assert.IsType<WireToGateRecoveryCommand>(command);
        Assert.Equal("ExampleNewMessage", translated.MessageType);
        Assert.Contains("E-1", translated.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public void AMessageNobodyRegisteredIsLeftToTheCallerRatherThanSwallowed()
    {
        WireToGateServerCommandTranslatorRegistry registry = new([new ExampleTranslator()]);

        Assert.False(registry.TryTranslate(
            Envelope("SlotOperationCommand", "{}"), out WireToGateServerCommand? command));
        Assert.Null(command);
    }

    [Fact]
    public void TwoTranslatorsForOneMessageTypeIsRefusedAtConstructionRatherThanSilentlyResolved()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new WireToGateServerCommandTranslatorRegistry(
                [new ExampleTranslator(), new ExampleTranslator()]));
        Assert.Contains("ExampleNewMessage", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProcessWideRegistryDoesNotClaimAnyMessageTheExistingSwitchAlreadyHandles()
    {
        // 注册表在 switch 之前被问到，所以两边都认领同一条消息就会静默改变行为。
        string[] handledByTheExistingSwitch =
        [
            "SublotEntryRequested",
            "SlotOperationCommand",
            "SlotOperationResumeCommand",
            "PreDepartureSafetyCheck",
            "ExceptionRecoverySessionSnapshot",
            "SublotRejected",
            "SlotOperationCommandRejected",
            "LoadCompensationCommand",
            "LoadCorrectionCommand",
            "FaultCargoRecoveryCommand",
        ];

        foreach (string messageType in handledByTheExistingSwitch)
        {
            Assert.DoesNotContain(
                messageType,
                WireToGateServerCommandTranslatorRegistry.Discovered.MessageTypes);
        }
    }

    private static WireToGateEnvelope Envelope(string messageType, string payloadJson)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        return new WireToGateEnvelope(
            WireToGateRelease.ProtocolVersion,
            WireToGateRelease.ProfileId,
            WireToGateRelease.ReleaseVersion,
            WireToGateRelease.ManifestSha256,
            messageType,
            Guid.NewGuid().ToString(),
            null,
            "AGV-TEST",
            1,
            new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero),
            payload.RootElement.Clone());
    }

    private sealed class ExampleTranslator : IWireToGateServerCommandTranslator
    {
        public string MessageType => "ExampleNewMessage";

        public WireToGateServerCommand Translate(WireToGateEnvelope envelope) =>
            new WireToGateRecoveryCommand(
                envelope.MessageType,
                envelope.MessageId,
                envelope.CorrelationId,
                envelope.SessionGeneration!.Value,
                envelope.SentAt,
                envelope.Payload.GetRawText());
    }
}
