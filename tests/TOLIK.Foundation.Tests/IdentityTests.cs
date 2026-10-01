using TOLIK.Infrastructure.Identity;

namespace TOLIK.Foundation.Tests;

public sealed class IdentityLifecycle
{
    [Fact]
    public void Session_token_is_stored_as_one_way_hash_material()
    {
        const string token = "identity-test-token";
        var hash = IdentityService.HashToken(token);

        Assert.NotEqual(token, hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, IdentityService.HashToken(token));
    }
}
