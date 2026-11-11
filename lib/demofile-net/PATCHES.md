# Vendored DemoFile.Net: local patches

This is [saul/demofile-net](https://github.com/saul/demofile-net) at commit `fd59701a998cf30a46adc4942e063d90de73c07a` (May 2026, NuGet 0.44.1), MIT licensed (see `LICENSE`). Only `DemoFile` and `DemoFile.Game.Deadlock` are included. The Deadlock schema JSON, which is used only by the SDK generator, was dropped.

The upstream schema predates several Deadlock patches, and without these changes current demos fail to parse. Every patched spot is marked with a `DeadlockClipper patch` comment.

| File | Change | Why |
|---|---|---|
| `DemoFile/FieldDecode.cs` | Integer and enum decoders honour `VarEncoder = fixedN` (N raw bits). `DecodeFixed64` is now internal. | Valve moved many small enums and ints (`SolidType_t`, `uint8`, ...) to `fixed8`. Reading them as varints desyncs the bit stream. |
| `DemoFile/FallbackDecoder.cs` | Unknown types decode by their encoding info: `fixedN`, otherwise an unsigned varint. This matches haste's default. | New typedefs such as `PlayerConnectedState`, `HeroBuildID_t` and `AnimationAlgorithm_t` aren't in the generated schema. |
| `DemoFile/FallbackDecoder.cs` | `CUtlBinaryBlock` is a varint length followed by raw bytes. | Used by `AnimGraph2SerializedPoseRecipeSlot_t.m_topology`. |
| `DemoFile/FallbackDecoder.cs` + `Sdk/DecoderSet.cs` | Struct, pointer and vector-of-struct fields whose type isn't generated are decoded from the serializer definition shipped in the demo (`TryGetDynamicDecoder`). | New structs such as `CCitadelPlayOfTheGame` and `AnimGraph2SerializedPoseRecipeSlot_t`. |
| `DemoFile/DemoParser.Abstract.cs`, `DemoParser.Entities.cs`, `DeadlockDemoParser.cs` | Unknown entity classes are created as plain `CBaseEntity` (`TryCreateFallbackEntity`). | Entities for heroes and abilities added after the schema (e.g. `CCitadel_Ability_Artist_*`). |
| `DemoFile/DemoParser.Entities.cs` | Removed `Debug.Assert(msg.LegacyIsDelta)`. | Current demos send deltas inside full updates, which haste also accepts. The assert would FailFast the process. |
| `DemoFile.Game.Deadlock/Sdk/Schema.cs` | `HeroID` and `MatchID` decode as unsigned varints. | They are now unsigned on the wire. Zigzag decoding halved match IDs and produced negative hero IDs. |
| `*.csproj`, `Directory.Build.props` | Target net10.0. Warnings are no longer errors. Dropped NerdBank.GitVersioning, SourceLink and doc generation. | Builds inside this repo without a git checkout of upstream. |

## Debugging the next Deadlock patch

When a game update breaks parsing, the integration test usually fails in one of two ways. It may throw (`Unrecognised serializer field`, `Delta on non-existent entity`), or the kill feed may stop matching the scoreboard. These environment variables help locate the field that's misread:

- `DEMOFILE_TRACE_ENTITIES=1` prints each entity's field paths, the bits each one consumed, and the next 32 raw bytes. The first field whose value looks wrong is right after the misread one.
- `DEMOFILE_DUMP_SERIALIZER=<ClassName>` prints a serializer's fields with their types and encodings, so you can map path indices to names. A class can have several versions, which are printed one after another.
- `DEMOFILE_TRACE_FALLBACK=1` lists fields that fell through to the encoding-based fallback.

[haste](https://github.com/blukai/haste) (Rust) tracks Deadlock closely, and its `fieldmetadata.rs` and `fielddecoder.rs` are a good reference for new encodings.

If upstream DemoFile.Net ships a schema update, switching back to the NuGet package is preferable. Replace the `ProjectReference` in `src/DeadlockClipper.Core/DeadlockClipper.Core.csproj` and re-run the integration test against fresh demos.
