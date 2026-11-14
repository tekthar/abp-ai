using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Volo.Abp.Security.Claims;

namespace AbpAi.SampleHost;

/// <summary>The sample's sign-in: the account named in the X-Account header. A real host signs people in its own way.</summary>
public sealed class XAccount(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Name = "XAccount";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Account", out var value) || !Guid.TryParse(value, out Guid id))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        ClaimsIdentity identity = new([new Claim(AbpClaimTypes.UserId, id.ToString())], Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Name)));
    }
}
