using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TOLIK.Contracts;

namespace TOLIK.Core.Host.Tests;

/// <summary>
/// Trusted-person enrollment flow (docs/SECURITY.md §18): the Owner mints a
/// single-use enrollment token for an explicit role; the holder enrolls once with it.
/// Tokens are single-use, expiring and audited; non-owners cannot mint; the enrolled
/// Family member stays scoped (owner's private memory and audit remain inaccessible).
/// </summary>
public sealed class EnrollmentFlowTests
{
    private static async Task<string> EnrollAsOwnerAsync(HttpClient client)
    {
        var sessionToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", sessionToken);
        return sessionToken;
    }

    [Fact]
    public async Task Owner_issues_token_and_family_enrolls_once()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        await EnrollAsOwnerAsync(client);

        var tokenResponse = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "family", reason = "жена" });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var tokenPayload = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Family", tokenPayload.GetProperty("role").GetString());
        var enrollmentToken = tokenPayload.GetProperty("enrollmentToken").GetString();

        // Family member enrolls once with the token.
        var enroll = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken,
            installationId = "family-device-1",
            publicKey = "-----BEGIN PUBLIC KEY-----TEST-----END PUBLIC KEY-----"
        });
        Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        var enrolled = await enroll.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Family", enrolled.GetProperty("role").GetString());

        // The token is single-use: reuse is rejected.
        var reuse = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken,
            installationId = "family-device-2",
            publicKey = "-----BEGIN PUBLIC KEY-----TEST2-----END PUBLIC KEY-----"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    [Fact]
    public async Task Unknown_or_invalid_token_is_rejected()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        await EnrollAsOwnerAsync(client);

        var enroll = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken = "made-up-token",
            installationId = "device-x",
            publicKey = "-----BEGIN PUBLIC KEY-----X-----END PUBLIC KEY-----"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, enroll.StatusCode);
    }

    [Fact]
    public async Task Non_owner_cannot_mint_enrollment_tokens()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var ownerToken = await TestEnrollment.EnrollOwnerAsync(client);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);

        // Owner mints a token and enrolls a Family member.
        var tokenResponse = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "trustedguest", reason = "friend" });
        Assert.Equal(HttpStatusCode.OK, tokenResponse.StatusCode);
        var enrollmentToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentToken").GetString();
        await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken,
            installationId = "guest-device-1",
            publicKey = "-----BEGIN PUBLIC KEY-----GUEST-----END PUBLIC KEY-----"
        });

        // The enrolled TrustedGuest authenticates and tries to mint a token → 403.
        var guestEnroll = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken = enrollmentToken, // consumed — use a fresh one
            installationId = "x",
            publicKey = "x"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, guestEnroll.StatusCode);

        // Re-mint a fresh token as owner, enroll a second guest, then that guest attempts minting.
        var token2 = (await (await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "trustedguest", reason = "friend2" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentToken").GetString();
        var secondEnroll = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken = token2,
            installationId = "guest-device-2",
            publicKey = "-----BEGIN PUBLIC KEY-----GUEST2-----END PUBLIC KEY-----"
        });
        Assert.Equal(HttpStatusCode.OK, secondEnroll.StatusCode);
        var guestSession = (await secondEnroll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", guestSession);
        var mintAttempt = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "family", reason = "escalation" });
        Assert.Equal(HttpStatusCode.Forbidden, mintAttempt.StatusCode);
        // Owner still fine.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);
        var ownerMint = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "family", reason = "ok" });
        Assert.Equal(HttpStatusCode.OK, ownerMint.StatusCode);
    }

    [Fact]
    public async Task Invalid_role_is_rejected()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        await EnrollAsOwnerAsync(client);
        var response = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "owner", reason = "escalation" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var response2 = await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "superuser", reason = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response2.StatusCode);
    }

    [Fact]
    public async Task Family_member_stays_scoped_from_owner_private_memory()
    {
        using var factory = new CoreHostFactory();
        using var client = factory.CreateClient();
        var ownerToken = await EnrollAsOwnerAsync(client);

        // Owner creates a Private memory.
        var marker = "owner-private-" + Guid.NewGuid().ToString("N");
        var create = await client.PostAsJsonAsync("/memory", new { scope = "Private", content = marker, source = "test", confidence = 0.9 });
        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        // Family member enrolls.
        var enrollmentToken = (await (await client.PostAsJsonAsync("/auth/enrollment-token", new { role = "family", reason = "жена" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enrollmentToken").GetString();
        var familyEnroll = await client.PostAsJsonAsync("/auth/enroll", new
        {
            enrollmentToken,
            installationId = "family-device-1",
            publicKey = "-----BEGIN PUBLIC KEY-----FAM-----END PUBLIC KEY-----"
        });
        Assert.Equal(HttpStatusCode.OK, familyEnroll.StatusCode);
        var familySession = (await familyEnroll.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sessionToken").GetString();

        // Family searches for the owner's private marker → not visible (§18/§19).
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", familySession);
        var search = await (await client.GetAsync("/memory/search?q=" + Uri.EscapeDataString(marker))).Content.ReadAsStringAsync();
        Assert.DoesNotContain(marker, search, StringComparison.Ordinal);

        // Owner still sees it.
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ownerToken);
        var ownerSearch = await (await client.GetAsync("/memory/search?q=" + Uri.EscapeDataString(marker))).Content.ReadAsStringAsync();
        Assert.Contains(marker, ownerSearch, StringComparison.Ordinal);
    }
}
