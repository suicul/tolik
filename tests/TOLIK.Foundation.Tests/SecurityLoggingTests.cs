using Microsoft.Extensions.Logging;
using System.Text.Json;
using TOLIK.Infrastructure.Logging;

namespace TOLIK.Foundation.Tests;

public sealed class SecurityLogging
{
    [Fact]
    public void RedactsSecrets()
    {
        const string apiKey = "TASK4_API_KEY_SENTINEL_8c14fbe3";
        const string password = "TASK4_PASSWORD_SENTINEL_f0945ea1";
        var originalOutput = Console.Out;
        using var capturedOutput = new StringWriter();

        try
        {
            Console.SetOut(capturedOutput);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddTolikSecurityLogging());
            var logger = loggerFactory.CreateLogger("SecurityLogging.RedactsSecrets");

            logger.LogInformation(
                "Request {RequestPath} with password={Password}",
                $"/health?api_key={apiKey}",
                password);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var log = capturedOutput.ToString();
        Assert.DoesNotContain(apiKey, log, StringComparison.Ordinal);
        Assert.DoesNotContain(password, log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(log);
        var root = document.RootElement;
        Assert.Equal("SecurityLogging.RedactsSecrets", root.GetProperty("category").GetString());
        Assert.Equal("[REDACTED]", root.GetProperty("properties").GetProperty("Password").GetString());
        Assert.Equal(
            "/health?api_key=[REDACTED]",
            root.GetProperty("properties").GetProperty("RequestPath").GetString());
    }

    [Fact]
    public void RedactsAuthorizationAndPrivateKeyFromExceptions()
    {
        const string bearerToken = "TASK4_BEARER_SENTINEL_6ac4dbd2";
        const string privateKey = "TASK4_PRIVATE_KEY_SENTINEL_e0a5c9d7";
        var originalOutput = Console.Out;
        using var capturedOutput = new StringWriter();

        try
        {
            Console.SetOut(capturedOutput);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddTolikSecurityLogging());
            var logger = loggerFactory.CreateLogger("SecurityLogging.ExceptionRedaction");
            var exception = new InvalidOperationException(
                $"Authorization: Bearer {bearerToken}; -----BEGIN PRIVATE KEY-----{privateKey}-----END PRIVATE KEY-----");

            logger.LogError(exception, "Provider failed with {Authorization}", $"Bearer {bearerToken}");
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var log = capturedOutput.ToString();
        Assert.DoesNotContain(bearerToken, log, StringComparison.Ordinal);
        Assert.DoesNotContain(privateKey, log, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", log, StringComparison.Ordinal);
        JsonDocument.Parse(log).Dispose();
    }

    [Fact]
    public void RedactsOpaqueStructuredSecretsAndPreservesSafeScope()
    {
        const string opaqueSecret = "TASK4_OPAQUE_SENTINEL_76ee2a";
        const string spacedSecret = "TASK4 QUOTED SECRET 19f0a7";
        const string metadataSecret = "TASK4_METADATA_SENTINEL_cbb103";
        var originalOutput = Console.Out;
        using var capturedOutput = new StringWriter();

        try
        {
            Console.SetOut(capturedOutput);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddTolikSecurityLogging());
            var logger = loggerFactory.CreateLogger($"SecurityLogging?token={metadataSecret}");
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = "task-4-correlation"
            });

            logger.LogInformation(
                new EventId(73, $"password={metadataSecret}"),
                "Credential {ApiKey}; password=\"{QuotedPassword}\"",
                opaqueSecret,
                spacedSecret);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var log = capturedOutput.ToString();
        Assert.DoesNotContain(opaqueSecret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(spacedSecret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(metadataSecret, log, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(log);
        var root = document.RootElement;
        Assert.Equal("task-4-correlation", root.GetProperty("scopes")[0].GetProperty("CorrelationId").GetString());
    }

    [Fact]
    public void RedactsKnownSensitiveValuesAcrossEveryOutputSlot()
    {
        const string knownSecret = "TASK4_PROPAGATED_SENTINEL_f83a41";
        const string scopeSecret = "TASK4_SCOPE_SENTINEL_17f4b0";
        const string pemBody = "TASK4_MULTILINE_PEM_SENTINEL_29dce6";
        const string connectionPassword = "TASK4_CONNECTION_SENTINEL_0aa9bf";
        const string correlationId = "task-4-safe-correlation";
        var privateKey = $"-----BEGIN PRIVATE KEY-----\n{pemBody}\n-----END PRIVATE KEY-----";
        var connectionString = $"Host=fixture;Password={connectionPassword};Database=fixture";
        var originalOutput = Console.Out;
        using var capturedOutput = new StringWriter();

        try
        {
            Console.SetOut(capturedOutput);
            using var loggerFactory = LoggerFactory.Create(logging => logging.AddTolikSecurityLogging());
            var logger = loggerFactory.CreateLogger("SecurityLogging.KnownValuePropagation");
            using var scope = logger.BeginScope(new Dictionary<string, object?>
            {
                ["CorrelationId"] = correlationId,
                ["ApiKey"] = knownSecret,
                ["AccessToken"] = scopeSecret,
                ["ScopeCopy"] = scopeSecret
            });
            var exception = new InvalidOperationException(
                $"Provider repeated {knownSecret} and {scopeSecret}; {privateKey}");

            logger.LogError(
                new EventId(91, $"Event-{knownSecret}-{scopeSecret}"),
                exception,
                "Credential {ApiKey}; duplicate {OrdinaryCopy}; scope duplicate {ScopeEventCopy}; key {PemCopy}; database {ConnectionString}",
                knownSecret,
                knownSecret,
                scopeSecret,
                privateKey,
                connectionString);
        }
        finally
        {
            Console.SetOut(originalOutput);
        }

        var log = capturedOutput.ToString();
        Assert.DoesNotContain(knownSecret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(scopeSecret, log, StringComparison.Ordinal);
        Assert.DoesNotContain(pemBody, log, StringComparison.Ordinal);
        Assert.DoesNotContain(connectionPassword, log, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(log);
        var root = document.RootElement;
        Assert.Equal("SecurityLogging.KnownValuePropagation", root.GetProperty("category").GetString());
        Assert.Equal(correlationId, root.GetProperty("scopes")[0].GetProperty("CorrelationId").GetString());
        Assert.Contains("InvalidOperationException", root.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }
}
