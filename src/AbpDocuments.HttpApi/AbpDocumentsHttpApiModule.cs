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
using Volo.Abp.Authorization;
using Volo.Abp.Modularity;
using Volo.Abp.Users;

namespace AbpDocuments.HttpApi;

/// <summary>
/// AbpDocuments' API over HTTP: <c>[DependsOn(typeof(AbpDocumentsHttpApiModule))]</c> in the host's module, and
/// <see cref="DocumentsHttpOptions"/> for the routes' prefix and what a sensitive action needs.
/// </summary>
[DependsOn(typeof(AbpDocumentsModule), typeof(AbpAspNetCoreMvcModule))]
public class AbpDocumentsHttpApiModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context) =>
        PreConfigure<IMvcBuilder>(mvc => mvc.AddApplicationPartIfNotExists(typeof(AbpDocumentsHttpApiModule).Assembly));

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.TryAddTransient<IDocumentsCurrentActor, CurrentUserDocumentsActor>();
        context.Services.AddTransient<IConfigureOptions<MvcOptions>, DocumentsHttpMvcSetup>();
        // The codes as the API means them; ABP would answer each with 403.
        Configure<AbpExceptionHttpStatusCodeOptions>(o =>
        {
            o.Map(DocumentsErrorCodes.Forbidden, HttpStatusCode.Forbidden);
            o.Map(DocumentsErrorCodes.Invalid, HttpStatusCode.BadRequest);
            o.Map(DocumentsErrorCodes.Unsupported, HttpStatusCode.BadRequest);
            o.Map(DocumentsErrorCodes.NotFound, HttpStatusCode.NotFound);
        });
    }
}

/// <summary>How the host serves the documents API (<see cref="AbpDocumentsHttpApiModule"/>).</summary>
public sealed class DocumentsHttpOptions
{
    /// <summary>The routes' prefix, the whole path before them: <c>api/documents</c> by default.</summary>
    public string RoutePrefix { get; set; } = "api/documents";

    /// <summary>An authorization policy every action needs; null (the default): a signed-in caller. What they may do is still the host's <see cref="IDocumentsRights"/>.</summary>
    public string? Policy { get; set; }

    /// <summary>
    /// What changing a document's class needs besides (<see cref="DocumentsSensitiveAttribute"/>): the host adds it,
    /// such as a step-up (<c>action =&gt; action.Filters.Add(new StepUpAttribute())</c>). By default nothing.
    /// </summary>
    public Action<ActionModel>? Sensitive { get; set; }

    /// <summary>Anything more on every action (endpoint metadata, a filter of the host's). By default nothing.</summary>
    public Action<ActionModel>? Configure { get; set; }

    /// <summary>The actions' OpenAPI group: null (the default) for the host's own document, a name for a document of their own.</summary>
    public string? OpenApiGroupName { get; set; }
}

/// <summary>An action that changes where a document's text may go, for <see cref="DocumentsHttpOptions.Sensitive"/>.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DocumentsSensitiveAttribute : Attribute;

/// <summary>Who calls the API, as the package's services take them: the account. The default is the signed-in user's id.</summary>
public interface IDocumentsCurrentActor
{
    Guid Get();
}

/// <summary>The signed-in user, by their id (ABP's <see cref="ICurrentUser"/>).</summary>
public sealed class CurrentUserDocumentsActor(ICurrentUser currentUser) : IDocumentsCurrentActor
{
    public Guid Get() => currentUser.Id ?? throw new AbpAuthorizationException("Sign in to see documents.");
}

/// <summary>ABP's error answer for every action of the package's, a file's included (ABP's own filter skips those unless the caller asked for JSON).</summary>
internal sealed class DocumentsExceptionFilter : AbpExceptionFilter
{
    protected override bool ShouldHandleException(ExceptionContext context) => true;
}

/// <summary>Puts <see cref="DocumentsHttpConvention"/> into MVC once the host's <see cref="DocumentsHttpOptions"/> are known.</summary>
internal sealed class DocumentsHttpMvcSetup(IOptions<DocumentsHttpOptions> options) : IConfigureOptions<MvcOptions>
{
    public void Configure(MvcOptions mvc) => mvc.Conventions.Add(new DocumentsHttpConvention(options.Value));
}

/// <summary>The package's controller under the host's prefix, behind its policy, visible to the API explorer in the host's group.</summary>
internal sealed class DocumentsHttpConvention(DocumentsHttpOptions options) : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        AttributeRouteModel prefix = new(new RouteAttribute(options.RoutePrefix.Trim('/')));
        AuthorizationPolicy signedIn = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (ControllerModel controller in application.Controllers.Where(c => c.ControllerType.Assembly == typeof(DocumentsHttpConvention).Assembly))
        {
            foreach (SelectorModel selector in controller.Selectors)
            {
                selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(prefix, selector.AttributeRouteModel);
            }

            controller.Filters.Add(options.Policy is { Length: > 0 } policy ? new AuthorizeFilter(policy) : new AuthorizeFilter(signedIn));
            controller.Filters.Add(new DocumentsExceptionFilter());
            controller.ApiExplorer.IsVisible = true;
            controller.ApiExplorer.GroupName = options.OpenApiGroupName;
            foreach (ActionModel action in controller.Actions)
            {
                if (action.Attributes.OfType<DocumentsSensitiveAttribute>().Any())
                {
                    options.Sensitive?.Invoke(action);
                }

                options.Configure?.Invoke(action);
            }
        }
    }
}
