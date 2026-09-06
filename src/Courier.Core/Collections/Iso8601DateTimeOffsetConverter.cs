using System.Globalization;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.Serialization;

namespace Courier.Core.Collections;

/// <summary>
/// Writes a timestamp as one ISO-8601 scalar. STOR-02.
/// </summary>
/// <remarks>
/// YamlDotNet has no built-in handling for <see cref="DateTimeOffset"/> and falls back to treating
/// it as an object, which writes out Ticks, DayOfWeek, TotalOffsetMinutes and eighteen other
/// properties. That is not a format any other language can read back, and it makes a one-field
/// change look like a twenty-line diff.
/// </remarks>
public sealed class Iso8601DateTimeOffsetConverter : IYamlTypeConverter
{
    private const string Format = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public bool Accepts(Type type) =>
        type == typeof(DateTimeOffset) || type == typeof(DateTimeOffset?);

    public object? ReadYaml(IParser parser, Type type, ObjectDeserializer rootDeserializer)
    {
        var scalar = parser.Consume<Scalar>();

        if (string.IsNullOrWhiteSpace(scalar.Value))
        {
            return type == typeof(DateTimeOffset?) ? null : default(DateTimeOffset);
        }

        return DateTimeOffset.Parse(
            scalar.Value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
    }

    public void WriteYaml(IEmitter emitter, object? value, Type type, ObjectSerializer serializer)
    {
        if (value is not DateTimeOffset timestamp)
        {
            emitter.Emit(new Scalar(string.Empty));
            return;
        }

        emitter.Emit(new Scalar(timestamp.ToString(Format, CultureInfo.InvariantCulture)));
    }
}
