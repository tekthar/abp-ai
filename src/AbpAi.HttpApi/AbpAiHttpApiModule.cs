using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Volo.Abp.AspNetCore.ExceptionHandling;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.ExceptionHandling;
using Volo.Abp.Modularity;

namespace AbpAi.HttpApi;

/// <summary>
/// AbpAi's admin API over HTTP: <c>[DependsOn(typeof(AbpAiHttpApiModule))]</c> in the host's module, and
/// <see cref="AiHttpOptions"/> for the routes' prefix, the policy and what a sensitive action needs.
/// </summary>
[DependsOn(typeof(AbpAiModule), typeof(AbpAspNetCoreMvcModule))]
public class AbpAiHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<IMvcBuilder>(mvc => mvc.AddApplicationPartIfNotExists(typeof(AbpAiHttpApiModule).Assembly));

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddTransient<IAiCurrentActor, CurrentUserAiActor>();
        context.Services.AddTransient<IConfigureOptions<MvcOptions>, AiHttpMvcSetup>();
        // The services' codes as the API means them; ABP would answer each with 403.
        Configure<AbpExceptionHttpStatusCodeOptions>(o =>
        {
            o.Map(AiErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            o.Map(AiErrorCodes.Invalid, HttpStatusCode.BadRequest);
            o.Map(AiErrorCodes.HostConnections, HttpStatusCode.Conflict);
        });
    }
}

/// <summary>
/// ABP's error answer (its JSON, the status of the code) for every action of the package's: ABP's own filter skips an
/// action that returns a file or a result of its own unless the caller asked for JSON, so a refusal there was a 500.
/// </summary>
internal sealed class AiExceptionFilter : AbpExceptionFilter
{
    protected override bool ShouldHandleException(ExceptionContext context) => true;
}

/// <summary>Puts <see cref="AiHttpConvention"/> into MVC once the host's <see cref="AiHttpOptions"/> are known.</summary>
internal sealed class AiHttpMvcSetup(IOptions<AiHttpOptions> options) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions mvc) => mvc.Conventions.Add(new AiHttpConvention(options.Value));
}

/// <summary>
/// The package's controllers under the host's prefix, behind its policy, visible to the API explorer in the host's
/// group, with the host's additions on the sensitive actions and on every action.
/// </summary>
internal sealed class AiHttpConvention(AiHttpOptions options) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        AttributeRouteModel prefix = new(new RouteAttribute(options.RoutePrefix.Trim('/')));
        AuthorizationPolicy signedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (ControllerModel controller in application.Controllers.Where(c => c.ControllerType.Assembly == typeof(AiHttpConvention).Assembly))
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(prefix, selector.AttributeRouteModel);
            }

            controller.Filters.Add(options.Policy is { Length: > 0 } policy ? new AuthorizeFilter(policy) : new AuthorizeFilter(signedIn));
            controller.Filters.Add(new AiExceptionFilter());
            controller.ApiExplorer.IsVisible = true;
            controller.ApiExplorer.GroupName = options.OpenApiGroupName;
            foreach (ActionModel action in controller.Actions)
            {
                if (action.Attributes.OfType<AiSensitiveAttribute>().Any())
                {
                    options.Sensitive?.Invoke(action);
                }

                options.Configure?.Invoke(action);
            }
        }
    }
}
