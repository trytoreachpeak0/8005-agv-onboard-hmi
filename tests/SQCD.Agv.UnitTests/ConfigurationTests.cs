using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.UnitTests;

public sealed class ConfigurationTests
{
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
}
