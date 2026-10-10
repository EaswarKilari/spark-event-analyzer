using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SparkEventAnalyzer.Models;

/// <summary>
/// Tolerant <see cref="DateTimeOffset"/>? converter for Spark History Server timestamps.
/// Spark returns values like <c>"2026-09-18T06:09:22.113GMT"</c> (trailing "GMT" instead of a
/// numeric offset), which the default System.Text.Json converter cannot parse. This converter
/// handles that format, standard ISO-8601 strings, and epoch-millisecond numbers, and never
/// throws (unparseable values become null) so a single odd timestamp can't fail the whole request.
/// </summary>
public sealed class SparkDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                // Epoch milliseconds (Spark also exposes *Epoch fields in this form).
                return reader.TryGetInt64(out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

            case JsonTokenType.String:
                var s = reader.GetString();
                if (string.IsNullOrWhiteSpace(s))
                    return null;

                if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                    return dto;

                // Spark format: "yyyy-MM-ddTHH:mm:ss.fffGMT" — treat trailing GMT as UTC.
                if (s.EndsWith("GMT", StringComparison.OrdinalIgnoreCase))
                {
                    var utc = string.Concat(s.AsSpan(0, s.Length - 3), "Z");
                    if (DateTimeOffset.TryParse(utc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out dto))
                        return dto;
                }

                return null; // tolerant: don't fail the whole payload on one bad timestamp
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
            writer.WriteStringValue(value.Value);
        else
            writer.WriteNullValue();
    }
}
