using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeadlockClipper.Api;

public static class Json
{
    public static void Configure(JsonSerializerOptions o)
    {
        o.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new UInt64AsStringConverter());
    }

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(o);
        return o;
    }

    /// <summary>64-bit Steam IDs exceed JavaScript's safe integer range, so they travel as strings.</summary>
    private sealed class UInt64AsStringConverter : JsonConverter<ulong>
    {
        public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String ? ulong.Parse(reader.GetString()!) : reader.GetUInt64();

        public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
