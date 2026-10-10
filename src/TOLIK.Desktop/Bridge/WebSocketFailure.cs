using System.Net.Http;

namespace TOLIK.Desktop.Bridge;

/// <summary>How a failed connection attempt should be treated.</summary>
public enum CoreLinkFailure
{
    /// <summary>Network / server / protocol trouble — reconnect with backoff.</summary>
    Transport,

    /// <summary>Core rejected the credential (HTTP 401/403 on the WS handshake).
    /// Never reconnect blindly: auth failures surface honestly in the debug overlay.</summary>
    AuthRejected,
}

/// <summary>
/// Classifies a failed ClientWebSocket handshake. The BCL surfaces a rejected HTTP
/// handshake as a WebSocketException whose status code is only recoverable from the
/// exception message ("The server returned status code '401': ...") or from an inner
/// HttpRequestException (which carries .StatusCode) — both shapes are checked across
/// the whole inner-exception chain. Anything unrecognizable classifies as Transport:
/// a missed 401 costs a polite backed-off retry, while a false "auth rejected" would
/// stop the link permanently — the conservative direction for THIS failure is
/// Transport, for AUTH the dedicated terminal path exists in the client.
/// </summary>
public static class WebSocketFailure
{
    public static CoreLinkFailure Classify(Exception? exception)
    {
        Exception? current = exception;
        int depth = 0;
        while (current is not null && depth < 8)
        {
            if (current is HttpRequestException http
                && http.StatusCode is System.Net.HttpStatusCode.Unauthorized
                    or System.Net.HttpStatusCode.Forbidden)
            {
                return CoreLinkFailure.AuthRejected;
            }

            string? message = current.Message;
            if (message is not null
                && (message.Contains("status code '401'", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("status code '403'", StringComparison.OrdinalIgnoreCase)))
            {
                return CoreLinkFailure.AuthRejected;
            }

            current = current.InnerException;
            depth++;
        }

        return CoreLinkFailure.Transport;
    }
}
