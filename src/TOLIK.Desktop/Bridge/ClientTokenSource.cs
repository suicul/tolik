using System.IO;
using System.Net.Http;
using TOLIK.Desktop.Identity;

namespace TOLIK.Desktop.Bridge;

/// <summary>
/// Chooses HOW this installation authenticates. The order is deliberate and must not be
/// reduced to "try everything until something works":
///
/// 1. <b>Device identity present</b> — the real path. The device signs a one-time
///    challenge; Core verifies it against the enrolled P-256 key and issues a session.
///    This works after Genesis, which is the state a real installation is always in.
/// 2. <b>Bootstrap code present</b> — first run on a fresh machine. Enrolls once, which
///    creates the identity used by path 1 from then on.
/// 3. <b>Dev token</b> — convenience ONLY, and only while Core still offers it. Core
///    refuses it outright once Genesis has run, so this cannot silently become the
///    production credential. Kept because the local development loop and the existing
///    desktop tests depend on it, and because removing it would make the client harder
///    to run against a throwaway Core. It is last, and it fails loudly.
///
/// The previous client had only path 3 while presenting itself as a finished client —
/// so against any real installation it could not authenticate at all.
/// </summary>
public sealed class ClientTokenSource : ISessionTokenSource, IDisposable
{
    private readonly ISessionTokenSource _device;
    private readonly ISessionTokenSource _dev;
    private bool _disposed;

    public ClientTokenSource(ISessionTokenSource device, ISessionTokenSource dev)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _dev = dev ?? throw new ArgumentNullException(nameof(dev));
    }

    /// <summary>
    /// Builds the chain from the environment: an existing device key wins, otherwise a
    /// registration code, otherwise the dev flow.
    /// </summary>
    public static ClientTokenSource Create(CoreLinkOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        DeviceIdentity? identity = null;
        try
        {
            // create: false — the client must NEVER mint a key as a side effect of
            // connecting. An unregistered install stays unregistered until the user
            // supplies the code, instead of quietly producing an identity Core will reject.
            identity = DeviceIdentity.LoadOrCreate(create: false);
        }
        catch (InvalidOperationException)
        {
            // Not registered yet (or the key belongs to another Windows account).
            identity = null;
        }

        string? bootstrapCode = Environment.GetEnvironmentVariable("TOLIK_ENROLL_CODE");
        var deviceSource = new DeviceTokenSource(options, identity, bootstrapCode, http);
        var devSource = new LocalTokenSource(options, http);
        return new ClientTokenSource(deviceSource, devSource);
    }

    /// <summary>True when a device identity exists — the UI can say "устройство зарегистрировано".</summary>
    public bool HasDeviceIdentity => _device is DeviceTokenSource { Identity: not null };

    public async Task<SessionToken?> AcquireAsync(CancellationToken cancellationToken)
    {
        // Path 1/2: the device path last-details WHY it failed; that reason is worth
        // reporting instead of masking it behind a dev-token attempt.
        SessionToken? viaDevice = await _device.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (viaDevice is not null)
        {
            return viaDevice;
        }

        SessionToken? viaDev = await _dev.AcquireAsync(cancellationToken).ConfigureAwait(false);
        if (viaDev is not null)
        {
            return viaDev;
        }

        return null;
    }

    public void Invalidate()
    {
        _device.Invalidate();
        _dev.Invalidate();
    }

    /// <summary>
    /// Prefers the device path's reason: "устройство отозвано" is the actionable fact,
    /// while the dev endpoint adds only "dev auto-token unavailable".
    /// </summary>
    public string? LastDetail => _device.LastDetail ?? _dev.LastDetail;

    /// <summary>
    /// Disposes both links. The device link owns the DPAPI-sealed signing key and the HTTP
    /// clients, so a caller that drops this object must be able to release them — the
    /// earlier version was not disposable, and the ownership call site therefore released
    /// nothing while looking as if it did.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_device as IDisposable)?.Dispose();
        (_dev as IDisposable)?.Dispose();
    }
}
