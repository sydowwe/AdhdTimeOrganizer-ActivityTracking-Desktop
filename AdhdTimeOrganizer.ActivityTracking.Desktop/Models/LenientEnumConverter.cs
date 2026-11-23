using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdhdTimeOrganizer.ActivityTracking.Desktop.Models;

/// <summary>
/// An enum in <c>config.json</c>, written as its member name like <see cref="JsonStringEnumConverter{TEnum}"/> writes it,
/// and read as leniently as that reads it (any case, or a number) — except that a value this build does not know does not
/// fail the whole file. It reads as <see cref="Unknown"/>, a value no member has, and <see cref="AppConfig.Normalize"/>
/// decides what it becomes: a newer build's config must not throw away an older build's privacy rules.
/// </summary>
public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    /// <summary>Not a member of any of the config's enums; <see cref="Enum.IsDefined{TEnum}(TEnum)"/> is false for it.</summary>
    public static TEnum Unknown { get; } = (TEnum)Enum.ToObject(typeof(TEnum), -1);

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Enum.TryParse<TEnum>(reader.GetString(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : Unknown;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var number) ? (TEnum)Enum.ToObject(typeof(TEnum), number) : Unknown;
            default:
                reader.Skip();
                return Unknown;
        }
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
