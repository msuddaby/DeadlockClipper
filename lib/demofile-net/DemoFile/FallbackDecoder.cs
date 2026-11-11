using System.Diagnostics.CodeAnalysis;
using DemoFile.Sdk;

namespace DemoFile;

public static class FallbackDecoder
{
    public readonly record struct Unit;

    public static bool TryCreate(
        string fieldName,
        FieldType fieldType,
        FieldEncodingInfo encodingInfo,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<Unit>? decoder,
        SerializerKey? serializerKey = null)
    {
        decoder = null;

        // DeadlockClipper patch: a field backed by a serializer (struct, pointer-to-struct or vector of structs)
        // whose type isn't generated — decode it from the serializer definition shipped in the demo.
        if (serializerKey is { } key && fieldType.ArrayLength == 0
            && !decoderSet.TryGetDecoderByName(fieldType.Name, out _, out _)
            && decoderSet.TryGetDynamicDecoder(key, out var structDecoder))
        {
            if (fieldType.IsPointer)
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                {
                    if (path.Length == 1) buffer.ReadOneBit(); // pointer set/unset
                    else structDecoder(default, path[1..], ref buffer);
                };
            }
            else if (fieldType.Name is "CNetworkUtlVectorBase" or "CUtlVectorEmbeddedNetworkVar" or "CUtlVector")
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                {
                    if (path.Length == 1) buffer.ReadUVarInt32(); // new size
                    else if (path.Length > 2) structDecoder(default, path[2..], ref buffer);
                };
            }
            else
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    structDecoder(default, path[1..], ref buffer);
            }
            return true;
        }

        // Fallback array
        if (fieldType.ArrayLength != 0)
        {
            // Special case strings
            if (fieldType.Name == "char")
            {
                var fieldDecoder = FieldDecode.CreateDecoder_string(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }

            if (!TryCreate(fieldName, fieldType with {ArrayLength = 0}, encodingInfo, decoderSet, out var innerDecoder, serializerKey))
                return false;

            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                var elementIdx = path[1];
                innerDecoder(default, path[2..], ref buffer);
            };
            return true;
        }

        // Fallback pointer
        if (fieldType.IsPointer)
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                if (path.Length == 1)
                {
                    var isSet = buffer.ReadOneBit();
                    if (isSet)
                    {
                        throw new NotImplementedException($"Cannot decode fallback pointer field ({fieldType} {fieldName}) set to a non-null value");
                    }
                }
                else
                {
                    throw new NotImplementedException($"Cannot decode inner field for fallback pointer ({fieldType} {fieldName})");
                }
            };
            return true;
        }

        if (fieldType.Name is "CNetworkUtlVectorBase" or "CUtlVectorEmbeddedNetworkVar" or "CUtlVector")
        {
            if (!TryCreate(fieldName, fieldType.GenericParameter!, encodingInfo, decoderSet, out var innerDecoder))
                return false;

            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            {
                // New size of vector
                if (path.Length == 1)
                {
                    buffer.ReadUVarInt32();
                }
                else
                {
                    var elementIdx = path[1];
                    innerDecoder(default, path[2..], ref buffer);
                }
            };
            return true;
        }

        switch (fieldType.Name)
        {
            case "Vector" or "VectorWS":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Quaternion":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Quaternion(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Vector2D":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector2D(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "Vector4D":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Vector4D(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "QAngle":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_QAngle(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "float32" or "CNetworkedQuantizedFloat" or "float64":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_float(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "bool":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    buffer.ReadOneBit();
                return true;
            }
            case "Color":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_Color(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "uint8" or "int8" or "int16" or "uint16" or "int32" or "uint32" or "int64" or "uint64" or "CStrongHandle" or "CEntityHandle" or "CHandle" or "HSequence" or "CSPlayerBlockingUseAction_t" or "BloodType" or "CGameSceneNodeHandle" or "ShatterPanelMode" or "CSWeaponState_t" or "WorldGroupId_t" or "attributeprovidertypes_t" or "DecalMode_t" or "CitadelAbilityUpgradeInfoPacked_t":
            {
                if (FieldDecode.TryGetFixedBits(encodingInfo, out var fixedBits))
                {
                    decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                        FieldDecode.ReadFixed(ref buffer, fixedBits);
                    return true;
                }
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    buffer.ReadUVarInt64();
                return true;
            }
            case "CUtlBinaryBlock":
            {
                // DeadlockClipper patch: varint byte length followed by raw bytes (matches haste's decoder).
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                {
                    var length = (int)buffer.ReadUVarInt32();
                    for (var i = 0; i < length; i++) buffer.ReadByte();
                };
                return true;
            }
            case "CUtlString" or "CUtlSymbolLarge":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_string(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CUtlStringToken":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CUtlStringToken(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CGlobalSymbol":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CGlobalSymbol(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CPlayerSlot":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CPlayerSlot(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "GameTime_t":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    FieldDecode.DecodeFloatNoscale(ref buffer);
                return true;
            }
            case "GameTick_t":
            {
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    buffer.ReadUVarInt32();
                return true;
            }
            case "CEntityIndex":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CEntityIndex(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CTransform":
            {
                var fieldDecoder = FieldDecode.CreateDecoder_CTransform(encodingInfo);
                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                    fieldDecoder(ref buffer);
                return true;
            }
            case "CRenderComponent":
            {

                decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                {
                    if (path.Length == 1)
                    {
                        var isSet = buffer.ReadOneBit();
                        if (isSet)
                        {
                            //throw new NotImplementedException($"Cannot decode fallback pointer field ({fieldType} {fieldName}) set to a non-null value");
                        }
                    }
                    else
                    {
                        throw new NotImplementedException($"Cannot decode inner field for fallback pointer ({fieldType} {fieldName})");
                    }
                };
                return true;
            }
            default:
                return TryCreateFallbackClassDecoder(fieldType, decoderSet, out decoder)
                       || TryCreateEncodingDecoder(fieldType, encodingInfo, out decoder)
                       || TryCreateHeuristicDecoder(fieldName, fieldType, out decoder);
        }
    }

    // DeadlockClipper patch: fields added by game updates after the vendored schema have unknown type names.
    // Their wire format is still fully described by the serializer's encoding info, so decode by that:
    // an explicit fixedN encoder reads N raw bits; otherwise any remaining type (struct types were already tried as classes above,
    // so these are enums/typedef'd ints like Foo_t or PlayerConnectedState) uses the default Source 2 integer encoding, an unsigned varint. Values are discarded — this only keeps
    // the bit stream in sync so the fields we do care about still parse.
    private static bool TryCreateEncodingDecoder(
        FieldType fieldType,
        FieldEncodingInfo encodingInfo,
        out SendNodeDecoder<Unit>? decoder)
    {
        var encoder = encodingInfo.VarEncoder;
        if (Environment.GetEnvironmentVariable("DEMOFILE_TRACE_FALLBACK") == "1")
            Console.Error.WriteLine($"[fallback-encoding] {fieldType} enc={encodingInfo}");
        if (FieldDecode.TryGetFixedBits(encodingInfo, out var bits))
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) => FieldDecode.ReadFixed(ref buffer, bits);
            return true;
        }

        if (encoder is null && encodingInfo.BitCount == 0)
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) => buffer.ReadUVarInt64();
            return true;
        }

        decoder = null;
        return false;
    }

    private static bool TryCreateHeuristicDecoder(
        string fieldName,
        FieldType fieldType,
        out SendNodeDecoder<Unit>? decoder)
    {
        // Heuristic: enum types. Example: EGrenadeThrowState, EFoo
        if (fieldType.Name.Length > 2
            && fieldType.Name[0] == 'E'
            && char.IsUpper(fieldType.Name[1]))
        {
            // See also FieldDecode.CreateDecoder_enum
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                buffer.ReadUVarInt64();
            return true;
        }

        // Heuristic: enum fields. Example: m_ePlayerFireEvent, m_eDoorState
        if (fieldName.Length > 4
            && fieldName.StartsWith("m_e")
            && char.IsUpper(fieldName[3]))
        {
            decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
                buffer.ReadUVarInt64();
            return true;
        }

        decoder = null;
        return false;
    }

    private static bool TryCreateFallbackClassDecoder(
        FieldType fieldType,
        DecoderSet decoderSet,
        [NotNullWhen(true)] out SendNodeDecoder<Unit>? decoder)
    {
        if (!decoderSet.TryGetDecoderByName(fieldType.Name, out var classType, out var innerDecoder))
        {
            decoder = null;
            return false;
        }

        var dummyInstance = Activator.CreateInstance(classType)!;

        decoder = (Unit _, ReadOnlySpan<int> path, ref BitBuffer buffer) =>
            innerDecoder(dummyInstance, path, ref buffer);

        return true;
    }
}
