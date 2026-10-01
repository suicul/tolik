using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TOLIK.Contracts;

/// <summary>
/// Enables System.Text.Json deserialization into ImmutableHashSet<T>,
/// used by AgentRegistration payloads posted to /agents/connect.
/// Without this converter minimal-API binding throws and the endpoint answers 5xx.
/// </summary>
public sealed class ImmutableSetConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType &&
        typeToConvert.GetGenericTypeDefinition() == typeof(ImmutableHashSet<>);

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter?)Activator.CreateInstance(
            typeof(ImmutableSetConverter<>).MakeGenericType(elementType),
            options);
    }

    private sealed class ImmutableSetConverter<T>(JsonSerializerOptions options) : JsonConverter<ImmutableHashSet<T>>
    {
        private readonly JsonConverter<T> _elementConverter = (JsonConverter<T>)options.GetConverter(typeof(T));

        public override ImmutableHashSet<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null) return null;
            if (reader.TokenType != JsonTokenType.StartArray)
                throw new JsonException($"Expected array for {nameof(ImmutableHashSet<T>)}.");

            var builder = ImmutableHashSet.CreateBuilder<T>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return builder.ToImmutable();
                var value = _elementConverter.Read(ref reader, typeof(T), options);
                if (value is not null) builder.Add(value);
            }
            throw new JsonException("Unterminated array.");
        }

        public override void Write(Utf8JsonWriter writer, ImmutableHashSet<T> value, JsonSerializerOptions options)
        {
            if (value is null) { writer.WriteNullValue(); return; }
            writer.WriteStartArray();
            foreach (var element in value)
                _elementConverter.Write(writer, element, options);
            writer.WriteEndArray();
        }
    }
}
