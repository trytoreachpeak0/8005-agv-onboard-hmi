using System.Reflection;
using SQCD.Agv.Contracts;
using SQCD.Agv.Core;

namespace SQCD.Agv.Infrastructure;

/// <summary>
/// 把一条服务端消息翻译成一个 <see cref="WireToGateServerCommand"/>。
/// </summary>
/// <remarks>
/// One implementation per message type, one file per implementation. Adding a message means adding a
/// file: <see cref="WireToGateServerCommandTranslatorRegistry"/> discovers implementations by
/// reflection, so <c>WireToGateSessionClient.cs</c> is not edited for a new message.
///
/// The fifteen message types that predate this seam are still translated by the switch inside that
/// file. They are not moved here -- moving them would be a large diff with no behaviour change to
/// review it against. The seam is consulted first, so anything new goes through it.
/// </remarks>
public interface IWireToGateServerCommandTranslator
{
    /// <summary>本翻译器负责的 <c>messageType</c>。</summary>
    string MessageType { get; }

    /// <summary>
    /// 翻译一条已通过信封校验的消息。载荷不合法时抛 <see cref="InvalidDataException"/>，
    /// 与既有 switch 分支的失败方式一致。
    /// </summary>
    WireToGateServerCommand Translate(WireToGateEnvelope envelope);
}

/// <summary>
/// 按 <c>messageType</c> 索引的翻译器集合。
/// </summary>
public sealed class WireToGateServerCommandTranslatorRegistry
{
    private readonly Dictionary<string, IWireToGateServerCommandTranslator> _byMessageType;

    public WireToGateServerCommandTranslatorRegistry(IEnumerable<IWireToGateServerCommandTranslator> translators)
    {
        ArgumentNullException.ThrowIfNull(translators);

        Dictionary<string, IWireToGateServerCommandTranslator> byMessageType = new(StringComparer.Ordinal);
        foreach (IWireToGateServerCommandTranslator translator in translators)
        {
            if (!byMessageType.TryAdd(translator.MessageType, translator))
            {
                throw new InvalidOperationException(
                    $"消息 {translator.MessageType} 注册了两个翻译器："
                    + $"{byMessageType[translator.MessageType].GetType().Name} 与 {translator.GetType().Name}。");
            }
        }
        _byMessageType = byMessageType;
    }

    /// <summary>本进程内由反射发现的那一份。</summary>
    public static WireToGateServerCommandTranslatorRegistry Discovered { get; } = Discover();

    public IReadOnlyCollection<string> MessageTypes => _byMessageType.Keys;

    public bool TryTranslate(WireToGateEnvelope envelope, out WireToGateServerCommand? command)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (_byMessageType.TryGetValue(envelope.MessageType, out IWireToGateServerCommandTranslator? translator))
        {
            command = translator.Translate(envelope);
            return true;
        }
        command = null;
        return false;
    }

    private static WireToGateServerCommandTranslatorRegistry Discover()
    {
        List<IWireToGateServerCommandTranslator> translators = [];
        foreach (Type type in typeof(WireToGateServerCommandTranslatorRegistry).Assembly.GetTypes())
        {
            if (type.IsAbstract
                || type.IsInterface
                || !typeof(IWireToGateServerCommandTranslator).IsAssignableFrom(type)
                || type.GetConstructor(Type.EmptyTypes) is null)
            {
                continue;
            }
            translators.Add((IWireToGateServerCommandTranslator)Activator.CreateInstance(type)!);
        }
        return new WireToGateServerCommandTranslatorRegistry(translators);
    }
}
