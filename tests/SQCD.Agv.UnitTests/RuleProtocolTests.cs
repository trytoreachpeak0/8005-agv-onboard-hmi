using SQCD.Agv.Contracts;

namespace SQCD.Agv.UnitTests;

public sealed class RuleProtocolTests
{
    [Fact]
    public void SerializationRoundTripPreservesEnvelopeAndPayload()
    {
        RuleEnvelope envelope = RuleProtocolSerializer.Create(
            RuleMessageTypes.ScanVerifyRequest,
            "AGV-01",
            "VISIT-01",
            new ScanVerifyRequestPayload("LOAD-001", "Scanner"),
            messageId: "MSG-001");

        string line = RuleProtocolSerializer.SerializeLine(envelope);
        RuleEnvelope restored = RuleProtocolSerializer.DeserializeLine(line);
        ScanVerifyRequestPayload payload = RuleProtocolSerializer.DeserializePayload<ScanVerifyRequestPayload>(restored);

        Assert.Equal("MSG-001", restored.MessageId);
        Assert.Equal("VISIT-01", restored.VisitId);
        Assert.Equal("LOAD-001", payload.Sublot);
        Assert.Equal("Scanner", payload.InputMethod);
    }

    [Fact]
    public void OversizedMessageIsRejected()
    {
        string oversized = new('x', RuleProtocolSerializer.MaxMessageBytes + 1);

        Assert.Throws<InvalidDataException>(() => RuleProtocolSerializer.DeserializeLine(oversized));
    }
}
