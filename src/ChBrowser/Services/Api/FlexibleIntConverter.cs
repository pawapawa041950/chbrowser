using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChBrowser.Services.Api;

/// <summary>
/// 5ch の bbsmenu.json は数値フィールドが文字列で来ることがある (例: "category_number":"1")。
/// 数値・文字列のどちらでも int? として読めるようにする。
/// </summary>
internal sealed class FlexibleIntConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.TryGetInt32(out var v) ? v : null;
            case JsonTokenType.String:
                var s = reader.GetString();
                return int.TryParse(s, out var sv) ? sv : null;
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
