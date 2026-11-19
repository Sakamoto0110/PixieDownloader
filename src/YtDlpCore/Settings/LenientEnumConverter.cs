using System.Text.Json;
using System.Text.Json.Serialization;

namespace YtDlpCore;

/// <summary>
/// Writes an enum as its name and reads it back forgivingly: a name this build doesn't know (written by a
/// newer one, before a downgrade) or anything else that isn't a member reads as the enum's first member,
/// <c>default(T)</c>. With <see cref="JsonStringEnumConverter{T}"/> that same value would throw and take the
/// whole settings file down to the backup, then to defaults. So each enum declares its default value first.
/// </summary>
public sealed class LenientEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String &&
            Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var named) &&
            Enum.IsDefined(named))
        {
            return named;
        }
        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt32(out var number) &&
            Enum.IsDefined(typeof(T), number))
        {
            return (T)Enum.ToObject(typeof(T), number);
        }
        reader.Skip();
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
