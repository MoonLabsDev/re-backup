using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ReBackup.Shared.Retention;

public enum RetentionPeriod { Daily, Weekly, Monthly, Yearly }

/// <summary>One entry of a plan's "retention" list.</summary>
public sealed class RetentionRule
{
    public RetentionPeriod Period { get; set; }

    /// <summary>
    /// Daily: none. Weekly: a weekday ("Sunday"). Monthly: a day number as text ("1".."31", "0" = last day,
    /// "-1" = the day before the last day, …). Yearly: "MM-DD". Whole numbers are stored as JSON numbers.
    /// </summary>
    [JsonConverter(typeof(RetentionAnchorConverter))]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Anchor { get; set; }

    /// <summary>How many slots that have a version this rule keeps.</summary>
    public int Keep { get; set; } = 1;
}

/// <summary>Reads an anchor that is either JSON text or a JSON whole number; writes whole numbers as numbers.</summary>
public sealed class RetentionAnchorConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number when reader.TryGetInt32(out var number) => number.ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException("A retention anchor must be text or a whole number."),
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
            writer.WriteNullValue();
        else if (int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
            writer.WriteNumberValue(number);
        else
            writer.WriteStringValue(value);
    }
}
