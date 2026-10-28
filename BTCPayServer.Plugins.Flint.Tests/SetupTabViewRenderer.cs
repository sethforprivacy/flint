using System.Buffers;
using System.Globalization;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Flint.Services;
using BTCPayServer.Plugins.Flint.Tests.Fakes;
using BTCPayServer.Security;
using Ganss.Xss;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BTCPayServer.Plugins.Flint.Tests;

/// <summary>
/// Renders the plugin's two <c>ln-payment-method-setup</c> extension-point partials — the tabhead and the
/// tab — out of the compiled plugin assembly, for the tests that pin which store each partial may read from.
/// </summary>
/// <remarks>
/// <para>
/// The pages are discovered as the <see cref="RazorCompiledItemMetadataAttribute"/> the plugin assembly
/// carries for each <c>.cshtml</c> path, so what executes is the real compiled view, not a transcription.
/// Each render runs the page against a real <see cref="SparkService"/> over <see cref="SparkServiceHarness"/>
/// fakes, with the authorisation read through BTCPay's own <c>SetStoreData</c>/<c>GetStoreDataOrNull</c>
/// channel — the same one core's <c>SetContextFilter</c> writes on a store-authorised request.
/// </para>
/// <para>
/// The stand-ins are plumbing around the view rather than in it. The <c>@inject</c>ed <see cref="Safe"/> is
/// built over a single-method <see cref="IJsonHelper"/> and an <see cref="IHtmlHelper"/> dispatch proxy that
/// do only what the production helpers do for the one call the tabhead makes — serialise with
/// <see cref="JsonSerializer"/>, wrap the result verbatim as HTML — and throw on anything else, so no other
/// helper surface can be quietly relied on. Of the tag helpers the extension-point markup actually matches,
/// BTCPay's CSP nonce helper on the inline <c>&lt;script&gt;</c> and the permission helper on its links run
/// for real: the factory and activator are the production ones, and the permission check passes through a
/// stub <see cref="IAuthorizationService"/> that grants, because whether a real user holds
/// CanModifyStoreSettings is core's authorization stack's question, not the partial's. The one genuinely
/// modelled piece is link generation: .NET 10's <c>AnchorTagHelper</c> builds its <c>href</c> through
/// <see cref="IHtmlGenerator"/>, and <see cref="LinkBuildingHtmlGenerator"/> composes one from the route
/// values the view actually computed — which is precisely how these tests see which store id the view put
/// into the link. Every other generator member throws, so a view that starts leaning on more markup than
/// these partials carry cannot pass quietly.
/// </para>
/// </remarks>
internal static class SetupTabViewRenderer
{
    /// <summary>The compiled item path the Razor compiler recorded for the tabhead partial.</summary>
    public const string TabheadItemPath = "/Views/Shared/Spark/LNPaymentMethodSetupTabhead.cshtml";

    /// <summary>The compiled item path the Razor compiler recorded for the tab partial.</summary>
    public const string TabItemPath = "/Views/Shared/Spark/LNPaymentMethodSetupTab.cshtml";

    /// <summary>
    /// Executes the compiled <c>LNPaymentMethodSetupTabhead</c> page over the given authorisation and
    /// model, and returns everything it wrote.
    /// </summary>
    public static Task<string> RenderTabheadAsync(
        SparkService service,
        string? authorisedStoreId,
        string modelStoreId) =>
        RenderAsync(TabheadItemPath, service, authorisedStoreId, modelStoreId);

    /// <summary>
    /// Executes the compiled <c>LNPaymentMethodSetupTab</c> page over the given authorisation and model,
    /// and returns everything it wrote.
    /// </summary>
    public static Task<string> RenderTabAsync(
        SparkService service,
        string? authorisedStoreId,
        string modelStoreId) =>
        RenderAsync(TabItemPath, service, authorisedStoreId, modelStoreId);

    /// <summary>
    /// Runs <c>StartAsync</c> so the service's startup gate opens, with a timeout so a regression that hangs
    /// startup fails the test instead of the test run.
    /// </summary>
    /// <remarks>
    /// Needed even by the suppressed cases: if the store guard were gone, the partial would reach
    /// <c>GetConnectionString</c>, which awaits the gate — the test must distinguish "refused" from "still
    /// waiting on a service nobody started".
    /// </remarks>
    public static void StartService(SparkServiceHarness harness)
    {
        if (!harness.Service.StartAsync(CancellationToken.None).Wait(TimeSpan.FromSeconds(30)))
        {
            Assert.Fail(
                "SparkService.StartAsync did not complete within 30s over the harness's fake SDK; the render "
                + "under test must be able to await the plugin's startup gate.");
        }
    }

    /// <summary>
    /// Executes the plugin's compiled page at <paramref name="compiledItemPath"/> over the given
    /// authorisation and model, and returns everything it wrote.
    /// </summary>
    /// <param name="compiledItemPath">
    /// The <c>.cshtml</c> path the Razor compiler recorded for the page, e.g.
    /// <c>/Views/Shared/Spark/LNPaymentMethodSetupTabhead.cshtml</c>.
    /// </param>
    /// <param name="service">The harness's service the page's <c>@inject</c>ed property is wired to.</param>
    /// <param name="authorisedStoreId">
    /// The store to authorise onto the request through <c>SetStoreData</c>, or <c>null</c> for a request
    /// that carries no authorised store at all.
    /// </param>
    /// <param name="modelStoreId">
    /// The store id bound into the form model — the value a POST can name arbitrarily, independent of the
    /// authorisation above.
    /// </param>
    public static async Task<string> RenderAsync(
        string compiledItemPath,
        SparkService service,
        string? authorisedStoreId,
        string modelStoreId)
    {
        // .NET 10's Razor compiler tags each compiled page class with a key/value metadata attribute;
        // the "Identifier" key carries the .cshtml path it was compiled from.
        var pageType = typeof(SparkPlugin).Assembly
            .GetTypes()
            .Single(t => t.GetCustomAttributes<RazorCompiledItemMetadataAttribute>()
                .Any(m => m.Key == "Identifier" && m.Value == compiledItemPath));
        var page = (RazorPage<LightningNodeViewModel>)Activator.CreateInstance(pageType)!;

        var viewData = new ViewDataDictionary<LightningNodeViewModel>(
            new EmptyModelMetadataProvider(), new ModelStateDictionary())
        {
            Model = new LightningNodeViewModel { CryptoCode = "BTC", StoreId = modelStoreId },
        };

        var httpContext = new DefaultHttpContext();
        if (authorisedStoreId is not null)
        {
            // The same channel SetContextFilter writes on a request core authorised for a store.
            httpContext.SetStoreData(new StoreData { Id = authorisedStoreId });
        }

        // Both partials' rendered branches contain anchors, and the plugin's @addTagHelper lines bind
        // core's permission helper and MVC's anchor helper to them (the tabhead's configured branch also
        // carries an inline <script>, matched by BTCPay's CSP nonce helper). The factory and activator
        // below are the production ones (internals, instantiated the way the DI registration does), so
        // the partials get exactly the tag helpers a real request would build for them; the services
        // added alongside are what those helpers activate from — the CSP policy bag the script helper
        // stamps its nonce into, a granting authorization service, the production output buffer the
        // tag-helper pipeline writes through, and the stand-in link generator documented on the class.
        httpContext.RequestServices = new ServiceCollection()
            .AddSingleton<ITagHelperFactory>(CreateDefaultTagHelperFactory())
            .AddSingleton<IViewBufferScope>(CreateViewBufferScope())
            .AddSingleton(new ContentSecurityPolicies())
            .AddSingleton<IAuthorizationService, GrantAllAuthorizationService>()
            .AddSingleton<IHttpContextAccessor>(new FixedHttpContextAccessor(httpContext))
            .AddSingleton<IHtmlGenerator, LinkBuildingHtmlGenerator>()
            .BuildServiceProvider();

        var writer = new StringWriter(CultureInfo.InvariantCulture);
        page.ViewContext = new ViewContext
        {
            HttpContext = httpContext,
            RouteData = new RouteData(),
            ViewData = viewData,
            Writer = writer,
        };
        page.ViewData = viewData;
        // RazorPage<TModel>.Model is read off ViewData.Model; setting the view data is what feeds the view.

        SetInjectProperty(page, nameof(SparkService), service);
        SetInjectProperty(page, "Safe", new Safe(
            DispatchProxy.Create<IHtmlHelper, RawOnlyHtmlHelper>(),
            new PlainJsonHelper(),
            new HtmlSanitizer(),
            new RichTextHtmlSanitizer(new HtmlSanitizer())));

        // RazorPageBase.HtmlEncoder is a [RazorInject] the view engine fills from DI (HtmlEncoder.Default
        // is what MVC registers); the tag-helper output pipeline encodes through it.
        SetInjectProperty(page, "HtmlEncoder", System.Text.Encodings.Web.HtmlEncoder.Default);

        await page.ExecuteAsync();
        return writer.ToString();
    }

    /// <summary>Sets one of the compiled page's <c>@inject</c> properties, failing loudly if it is not there.</summary>
    private static void SetInjectProperty(object page, string name, object value)
    {
        var property = page.GetType().GetProperty(
            name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"The compiled page {page.GetType().Name} has no '{name}' inject property to populate.");

        if (!property.CanWrite)
        {
            throw new InvalidOperationException(
                $"The compiled page's '{name}' inject property is not writable.");
        }

        property.SetValue(page, value);
    }

    /// <summary>
    /// The production <see cref="ITagHelperFactory"/> over the production activator, both internal and
    /// therefore reflection-instantiated rather than reimplemented — the point is that the partial gets
    /// exactly the tag helpers a real request would build for it.
    /// </summary>
    private static ITagHelperFactory CreateDefaultTagHelperFactory()
    {
        var assembly = typeof(ITagHelperFactory).Assembly;
        var activatorType = assembly.GetType(
            "Microsoft.AspNetCore.Mvc.Razor.Infrastructure.DefaultTagHelperActivator", throwOnError: true)!;
        var factoryType = assembly.GetType(
            "Microsoft.AspNetCore.Mvc.Razor.DefaultTagHelperFactory", throwOnError: true)!;
        return (ITagHelperFactory)Activator.CreateInstance(factoryType, Activator.CreateInstance(activatorType))!;
    }

    /// <summary>
    /// The production <see cref="IViewBufferScope"/> over the shared array pools — the instance MVC's own
    /// DI registers — needed because the tag-helper pipeline buffers element content.
    /// </summary>
    private static IViewBufferScope CreateViewBufferScope()
    {
        var type = typeof(IViewBufferScope).Assembly.GetType(
            "Microsoft.AspNetCore.Mvc.ViewFeatures.Buffers.MemoryPoolViewBufferScope", throwOnError: true)!;
        return (IViewBufferScope)Activator.CreateInstance(
            type, ArrayPool<ViewBufferValue>.Shared, ArrayPool<char>.Shared)!;
    }

    /// <summary>Serialises JSON exactly as the page needs; anything else about the helper is unused by these views.</summary>
    private sealed class PlainJsonHelper : IJsonHelper
    {
        public IHtmlContent Serialize(object? model) =>
            new HtmlString(JsonSerializer.Serialize(model));
    }

    /// <summary>Grants every permission check, so a link's presence shows what the view rendered, not who is asking.</summary>
    private sealed class GrantAllAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    /// <summary>Hands the permission helper the very context the render is running over.</summary>
    private sealed class FixedHttpContextAccessor(HttpContext httpContext) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = httpContext;
    }

    /// <summary>
    /// The one <see cref="IHtmlGenerator"/> member these partials' markup reaches through — their anchors
    /// carry <c>asp-controller</c>/<c>asp-action</c>/<c>asp-route-storeId</c>, which .NET 10's anchor tag
    /// helper turns into a <see cref="GenerateActionLink"/> call — and it returns exactly what the
    /// production generator returns for the same inputs in the shape these tests need: an href composed
    /// from the route values the view computed, so an assertion can read which store id the view resolved.
    /// Every other member throws: a view that starts reaching for forms, inputs or validation markup has
    /// grown past what this harness undertakes to model, and must say so loudly.
    /// </summary>
    private sealed class LinkBuildingHtmlGenerator : IHtmlGenerator
    {
        public string IdAttributeDotReplacement => ".";

        public TagBuilder GenerateActionLink(
            ViewContext viewContext,
            string linkText,
            string? actionName,
            string? controllerName,
            string? protocol,
            string? hostname,
            string? fragment,
            object? routeValues,
            object? htmlAttributes)
        {
            var route = new RouteValueDictionary(routeValues);
            var path = $"/{controllerName}/{actionName}";
            if (route.TryGetValue("storeId", out var storeId) && storeId is not null)
            {
                path += $"/{storeId}";
            }

            var tag = new TagBuilder("a");
            tag.Attributes["href"] =
                (protocol is null ? string.Empty : $"{protocol}://") + hostname + path + fragment;
            return tag;
        }

        public string Encode(string value) => throw Unused(nameof(Encode), "string");

        public string Encode(object value) => throw Unused(nameof(Encode), "object");

        public string FormatValue(object? value, string? format) => throw Unused(nameof(FormatValue));

        public IHtmlContent GenerateAntiforgery(ViewContext viewContext) => throw Unused(nameof(GenerateAntiforgery));

        public TagBuilder GenerateCheckBox(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, bool? isChecked, object? htmlAttributes) =>
            throw Unused(nameof(GenerateCheckBox));

        public TagBuilder GenerateForm(
            ViewContext viewContext, string? actionName, string? controllerName, object? routeValues, string? method, object? htmlAttributes) =>
            throw Unused(nameof(GenerateForm));

        public IHtmlContent GenerateGroupsAndOptions(string? optionLabel, IEnumerable<SelectListItem> selectList) =>
            throw Unused(nameof(GenerateGroupsAndOptions));

        public TagBuilder GenerateHidden(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, object? value, bool useViewData, object? htmlAttributes) =>
            throw Unused(nameof(GenerateHidden));

        public TagBuilder GenerateHiddenForCheckbox(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression) =>
            throw Unused(nameof(GenerateHiddenForCheckbox));

        public TagBuilder GenerateLabel(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, string? labelText, object? htmlAttributes) =>
            throw Unused(nameof(GenerateLabel));

        public TagBuilder GeneratePageForm(
            ViewContext viewContext, string pageName, string? pageHandler, object? routeValues, string? fragment, string? method, object? htmlAttributes) =>
            throw Unused(nameof(GeneratePageForm));

        public TagBuilder GeneratePageLink(
            ViewContext viewContext, string linkText, string pageName, string? pageHandler, string? protocol, string? hostname, string? fragment, object? routeValues, object? htmlAttributes) =>
            throw Unused(nameof(GeneratePageLink));

        public TagBuilder GeneratePassword(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, object? value, object? htmlAttributes) =>
            throw Unused(nameof(GeneratePassword));

        public TagBuilder GenerateRadioButton(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, object? value, bool? isChecked, object? htmlAttributes) =>
            throw Unused(nameof(GenerateRadioButton));

        public TagBuilder GenerateRouteForm(
            ViewContext viewContext, string? routeName, object? routeValues, string? method, object? htmlAttributes) =>
            throw Unused(nameof(GenerateRouteForm));

        public TagBuilder GenerateRouteLink(
            ViewContext viewContext, string linkText, string? routeName, string? protocol, string? hostName, string? fragment, object? routeValues, object? htmlAttributes) =>
            throw Unused(nameof(GenerateRouteLink));

        public TagBuilder GenerateSelect(
            ViewContext viewContext, ModelExplorer? modelExplorer, string? optionLabel, string expression, IEnumerable<SelectListItem>? selectList, bool allowMultiple, object? htmlAttributes) =>
            throw Unused(nameof(GenerateSelect));

        public TagBuilder GenerateSelect(
            ViewContext viewContext, ModelExplorer? modelExplorer, string? optionLabel, string expression, IEnumerable<SelectListItem>? selectList, ICollection<string>? currentValues, bool allowMultiple, object? htmlAttributes) =>
            throw Unused(nameof(GenerateSelect), "with currentValues");

        public TagBuilder GenerateTextArea(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, int rows, int columns, object? htmlAttributes) =>
            throw Unused(nameof(GenerateTextArea));

        public TagBuilder GenerateTextBox(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, object? value, string? format, object? htmlAttributes) =>
            throw Unused(nameof(GenerateTextBox));

        public TagBuilder GenerateValidationMessage(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, string? message, string? tag, object? htmlAttributes) =>
            throw Unused(nameof(GenerateValidationMessage));

        public TagBuilder GenerateValidationSummary(
            ViewContext viewContext, bool excludePropertyErrors, string? message, string? headerTag, object? htmlAttributes) =>
            throw Unused(nameof(GenerateValidationSummary));

        public ICollection<string> GetCurrentValues(
            ViewContext viewContext, ModelExplorer? modelExplorer, string expression, bool allowMultiple) =>
            throw Unused(nameof(GetCurrentValues));

        private static NotSupportedException Unused(string member, string detail = "") =>
            new(
                $"The rendered setup partials must not call IHtmlGenerator.{member}{detail}; "
                + "extend this stand-in deliberately if they ever do.");
    }
}

/// <summary>
/// An <see cref="IHtmlHelper"/> that does only what the production helper does for <c>Raw</c> — wrap the
/// value verbatim as HTML — because that is all <c>Safe.Json</c> calls. Every other member throws, so a
/// view that starts reaching for more cannot pass on a helper it was never given. Public because that is
/// what <see cref="DispatchProxy"/> requires of the type it derives its proxy from.
/// </summary>
public class RawOnlyHtmlHelper : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod is { Name: "Raw" } && targetMethod.GetParameters().Length == 1)
        {
            return new HtmlString(System.Convert.ToString(args![0], CultureInfo.InvariantCulture) ?? string.Empty);
        }

        throw new NotSupportedException(
            $"The rendered setup partial must not call IHtmlHelper.{targetMethod?.Name}; "
            + "extend the test double deliberately if it ever does.");
    }
}
