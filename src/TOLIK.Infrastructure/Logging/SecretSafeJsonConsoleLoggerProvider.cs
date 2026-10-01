using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TOLIK.Infrastructure.Logging;

public sealed class SecretSafeJsonConsoleLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly object _writeLock = new();
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();

    public ILogger CreateLogger(string categoryName) =>
        new SecretSafeJsonConsoleLogger(categoryName, this, Write);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

    public void Dispose()
    {
    }

    private void Write(string json)
    {
        lock (_writeLock)
        {
            Console.Out.WriteLine(json);
        }
    }

    private sealed class SecretSafeJsonConsoleLogger(
        string categoryName,
        SecretSafeJsonConsoleLoggerProvider provider,
        Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull =>
            provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            var sensitiveValues = new HashSet<string>(StringComparer.Ordinal);
            string? messageTemplate = null;

            if (state is IEnumerable<KeyValuePair<string, object?>> structuredState)
            {
                foreach (var property in structuredState)
                {
                    if (property.Key == "{OriginalFormat}")
                    {
                        messageTemplate = RenderValue(property.Value);
                        continue;
                    }

                    var renderedValue = RenderValue(property.Value);
                    if (SecretRedactor.IsSensitiveName(property.Key))
                    {
                        properties[property.Key] = SecretRedactor.Replacement;
                        if (renderedValue.Length > 0)
                        {
                            sensitiveValues.Add(renderedValue);
                        }
                    }
                    else
                    {
                        properties[property.Key] = renderedValue;
                    }
                }
            }

            var message = formatter(state, exception);
            var scopes = CaptureScopes(provider._scopeProvider, sensitiveValues);

            var entry = new LogEntry(
                DateTimeOffset.UtcNow,
                logLevel.ToString(),
                RedactKnownValues(categoryName, sensitiveValues),
                eventId.Id,
                RedactKnownValues(eventId.Name, sensitiveValues),
                RedactKnownValues(message, sensitiveValues),
                RedactKnownValues(messageTemplate, sensitiveValues),
                properties.Count == 0 ? null : RedactDictionary(properties, sensitiveValues),
                scopes.Count == 0
                    ? null
                    : scopes.Select(scope =>
                            (IReadOnlyDictionary<string, string>)RedactDictionary(scope, sensitiveValues))
                        .ToArray(),
                exception is null
                    ? null
                    : RedactKnownValues(exception.ToString(), sensitiveValues));

            write(JsonSerializer.Serialize(entry, JsonOptions));
        }

        private static string RenderValue(object? value) =>
            Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

        private static List<IReadOnlyDictionary<string, string>> CaptureScopes(
            IExternalScopeProvider scopeProvider,
            ISet<string> sensitiveValues)
        {
            var scopes = new List<IReadOnlyDictionary<string, string>>();
            scopeProvider.ForEachScope((scope, state) =>
            {
                var capturedScope = new Dictionary<string, string>(StringComparer.Ordinal);
                if (scope is IEnumerable<KeyValuePair<string, object?>> structuredScope)
                {
                    foreach (var property in structuredScope)
                    {
                        var renderedValue = RenderValue(property.Value);
                        if (SecretRedactor.IsSensitiveName(property.Key))
                        {
                            capturedScope[property.Key] = SecretRedactor.Replacement;
                            if (renderedValue.Length > 0)
                            {
                                state.SensitiveValues.Add(renderedValue);
                            }
                        }
                        else
                        {
                            capturedScope[property.Key] = renderedValue;
                        }
                    }
                }
                else
                {
                    capturedScope["Scope"] = RenderValue(scope);
                }

                state.Scopes.Add(capturedScope);
            }, (Scopes: scopes, SensitiveValues: sensitiveValues));
            return scopes;
        }

        private static Dictionary<string, string> RedactDictionary(
            IReadOnlyDictionary<string, string> values,
            IReadOnlySet<string> sensitiveValues)
        {
            var redacted = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                redacted[RedactKnownValues(value.Key, sensitiveValues)] =
                    RedactKnownValues(value.Value, sensitiveValues);
            }

            return redacted;
        }

        private static string RedactKnownValues(
            string? value,
            IReadOnlySet<string> sensitiveValues)
        {
            var redacted = value ?? string.Empty;
            foreach (var sensitiveValue in sensitiveValues.OrderByDescending(candidate => candidate.Length))
            {
                redacted = redacted.Replace(
                    sensitiveValue,
                    SecretRedactor.Replacement,
                    StringComparison.Ordinal);
            }

            return SecretRedactor.Redact(redacted);
        }
    }

    private sealed record LogEntry(
        DateTimeOffset Timestamp,
        string Level,
        string Category,
        int EventId,
        string? EventName,
        string Message,
        string? MessageTemplate,
        IReadOnlyDictionary<string, string>? Properties,
        IReadOnlyList<IReadOnlyDictionary<string, string>>? Scopes,
        string? Exception);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };
}
