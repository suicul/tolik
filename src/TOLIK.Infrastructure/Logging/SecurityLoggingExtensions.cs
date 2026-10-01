using Microsoft.Extensions.Logging;

namespace TOLIK.Infrastructure.Logging;

public static class SecurityLoggingExtensions
{
    public static ILoggingBuilder AddTolikSecurityLogging(this ILoggingBuilder logging)
    {
        logging.AddProvider(new SecretSafeJsonConsoleLoggerProvider());
        return logging;
    }
}
