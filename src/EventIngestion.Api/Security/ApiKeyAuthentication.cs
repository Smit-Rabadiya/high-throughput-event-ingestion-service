using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using EventIngestion.Api.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace EventIngestion.Api.Security;

/// <summary>Holds hashed keys; validation is constant-time and compares against every key (no early exit).</summary>
public sealed class ApiKeyValidator
{
    private readonly (string Name, byte[] Hash, string[] Scopes)[] _keys;

    public ApiKeyValidator(AuthOptions options)
    {
        _keys = [.. options.Keys.Select(k => (k.Name,
            k.KeySha256 is not null ? Convert.FromHexString(k.KeySha256) : SHA256.HashData(Encoding.UTF8.GetBytes(k.Key!)),
            k.Scopes))];
    }

    public bool TryValidate(string presented, out string name, out string[] scopes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(presented), hash);

        name = "";
        scopes = [];
        bool found = false;
        foreach (var k in _keys)
        {
            if (CryptographicOperations.FixedTimeEquals(hash, k.Hash))
            {
                name = k.Name;
                scopes = k.Scopes;
                found = true;
            }
        }
        return found;
    }
}

public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ApiKeyValidator validator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string ScopeClaim = "scope";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values)) return Task.FromResult(AuthenticateResult.NoResult());
        if (values.Count != 1 || string.IsNullOrEmpty(values[0]) || !validator.TryValidate(values[0]!, out var name, out var scopes))
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));

        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(scopes.Select(s => new Claim(ScopeClaim, s)));
        var identity = new ClaimsIdentity(claims, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = SchemeName;
        return Task.CompletedTask;
    }
}
