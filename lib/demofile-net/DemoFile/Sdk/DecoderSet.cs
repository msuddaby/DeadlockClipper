using System.Diagnostics.CodeAnalysis;

namespace DemoFile.Sdk;

public abstract class DecoderSet
{
    private readonly Dictionary<SerializerKey, object?> _cache = new(512);
    private readonly IReadOnlyDictionary<SerializerKey, Serializer> _serializers;

    protected DecoderSet(IReadOnlyDictionary<SerializerKey, Serializer> serializers)
    {
        _serializers = serializers;
    }

    public abstract bool TryGetDecoderByName(
        string className,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor), NotNullWhen(true)] out Type? classType,
        [NotNullWhen(true)] out SendNodeDecoder<object>? decoder);

    // DeadlockClipper patch: decoders for serializer types the generated schema doesn't know (new structs such as
    // CCitadelPlayOfTheGame). Built from the field list the demo itself ships; values are discarded, only the bit
    // stream is consumed. Path convention: path[0] is the field index *within* this serializer.
    private readonly Dictionary<SerializerKey, SendNodeDecoder<FallbackDecoder.Unit>> _dynamicCache = new();

    public bool TryGetDynamicDecoder(SerializerKey key, [NotNullWhen(true)] out SendNodeDecoder<FallbackDecoder.Unit>? decoder)
    {
        if (_dynamicCache.TryGetValue(key, out decoder))
            return true;

        if (!_serializers.TryGetValue(key, out var serializer))
        {
            decoder = null;
            return false;
        }

        // Register before building fields so self-referencing serializers resolve to this decoder.
        var fieldDecoders = new SendNodeDecoder<FallbackDecoder.Unit>?[serializer.Fields.Length];
        decoder = (FallbackDecoder.Unit unit, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
        {
            var fieldDecoder = fieldDecoders[path[0]]
                ?? throw new NotSupportedException($"Cannot decode field {serializer.Fields[path[0]]} of {serializer.Key.Name}");
            fieldDecoder(unit, path, ref buffer);
        };
        _dynamicCache[key] = decoder;

        for (var i = 0; i < serializer.Fields.Length; i++)
        {
            var field = serializer.Fields[i];
            fieldDecoders[i] = FallbackDecoder.TryCreate(field.VarName, field.VarType, field.FieldEncodingInfo, this, out var d, field.FieldSerializerKey)
                ? d
                : null;
        }

        return true;
    }

    public abstract bool TryCreateFallbackDecoder(
        SerializableField field,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<object>? decoder);

    protected abstract SendNodeDecoderFactory<T> GetFactory<T>();

    public SendNodeDecoder<object> GetDecoder(string className)
    {
        if (!TryGetDecoderByName(className, out _, out var decoder))
            throw new NotImplementedException($"Unknown send node class: {className}");

        return decoder;
    }

    public SendNodeDecoder<T> GetDecoder<T>(SerializerKey serializerKey)
    {
        if (!_cache.TryGetValue(serializerKey, out var decoder))
        {
            // null sentinel so that we're aware of cycles when calling GetDecoder
            _cache[serializerKey] = null;

            decoder = CreateDecoder<T>(serializerKey);
            _cache[serializerKey] = decoder;
        }

        if (decoder == null)
        {
            throw new Exception("Cycle detected!");
        }

        return (SendNodeDecoder<T>) decoder;
    }

    private SendNodeDecoder<T> CreateDecoder<T>(SerializerKey serializerKey)
    {
        if (!_serializers.TryGetValue(serializerKey, out var serializer))
        {
            return (T instance, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                throw new Exception($"Cannot deserialise field on {typeof(T).Name} (missing serializer)");
            };
        }

        var decoderFactory = GetFactory<T>();
        var fieldDecodersByIndex = serializer.Fields.Select(field => decoderFactory(field, this)).ToArray();

        return DecodeField;

        void DecodeField(T instance, ReadOnlySpan<int> path, ref BitBuffer buffer)
        {
#if DEBUG
            var _serializer = serializer;
#endif

            var fieldDecoder = fieldDecodersByIndex[path[0]];
            fieldDecoder(instance, path, ref buffer);
        }
    }
}
