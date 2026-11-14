using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace AbpAi.HttpApi;

/// <summary>How the host serves AbpAi's admin API (<see cref="AbpAiHttpApiModule"/>).</summary>
public sealed class AiHttpOptions
{
    /// <summary>The routes' prefix, the whole path before them: <c>api/ai</c> by default, <c>api/ask/ai</c> or <c>api/platform/ai</c> for a product that keeps its own.</summary>
    public string RoutePrefix { get; set; } = "api/ai";

    /// <summary>An authorization policy every action needs; null (the default): a signed-in caller. What they may do is still AbpAi's check through the host's <see cref="IAiRights"/>.</summary>
    public string? Policy { get; set; }

    /// <summary>
    /// What an action that changes settings or hands out data needs besides (<see cref="AiSensitiveAttribute"/>): the
    /// host adds it, such as AbpGuard's step-up (<c>action =&gt; action.Filters.Add(new StepUpAttribute())</c>). By default nothing.
    /// </summary>
    public Action<ActionModel>? Sensitive { get; set; }

    /// <summary>Anything more on every action (endpoint metadata, a filter of the host's). By default nothing.</summary>
    public Action<ActionModel>? Configure { get; set; }

    /// <summary>The actions' OpenAPI group: null (the default) for the host's own document, a name (<c>abpai</c>) for a document of their own.</summary>
    public string? OpenApiGroupName { get; set; }
}

/// <summary>An action that changes the gateway's settings or hands out its data, for <see cref="AiHttpOptions.Sensitive"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AiSensitiveAttribute : Attribute;
