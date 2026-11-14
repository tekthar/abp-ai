using Volo.Abp.Authorization;
using Volo.Abp.Users;

namespace AbpAi.HttpApi;

/// <summary>
/// Who calls the admin API, as AbpAi's services and the host's <see cref="IAiRights"/> take them. The default is the
/// signed-in user's id; a host whose rights read more (an ability, a principal) gives its own.
/// </summary>
public interface IAiCurrentActor
{
    Task<AiActor> GetAsync(CancellationToken cancellationToken);
}

/// <summary>The signed-in user, by their id (ABP's <see cref="ICurrentUser"/>).</summary>
public sealed class CurrentUserAiActor(ICurrentUser currentUser) : IAiCurrentActor
{
    public Task<AiActor> GetAsync(CancellationToken cancellationToken) =>
        currentUser.Id is { } id ? Task.FromResult(new AiActor(id)) : throw new AbpAuthorizationException("Sign in to manage the AI gateway.");
}
