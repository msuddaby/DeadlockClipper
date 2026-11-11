using System.Diagnostics.CodeAnalysis;
using DemoFile.Sdk;

namespace DemoFile;

public partial class DemoParser<TGameParser>
{
    protected abstract IReadOnlyDictionary<string, EntityFactory<TGameParser>> EntityFactories { get; }

    protected internal abstract ref EntityEvents<CEntityInstance<TGameParser>, TGameParser> EntityInstanceEvents { get; }

    protected abstract DecoderSet CreateDecoderSet(IReadOnlyDictionary<SerializerKey, Serializer> serializers);

    protected abstract bool ParseNetMessage(int msgType, ReadOnlySpan<byte> msgBuf);

    /// <summary>
    /// DeadlockClipper patch: called for entity classes the generated schema doesn't know (e.g. abilities for
    /// heroes added after the schema was generated). Return a generic factory + decoder so the entity can still
    /// be created and its fields consumed, instead of aborting the whole parse.
    /// </summary>
    protected virtual bool TryCreateFallbackEntity(
        string networkName,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out EntityFactory<TGameParser>? factory,
        [NotNullWhen(true)] out SendNodeDecoder<object>? decoder)
    {
        factory = null;
        decoder = null;
        return false;
    }
}
