using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Core.Domain.Localization;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Web.Framework;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.Sattleme.Infrastructure;

/// <summary>
/// The plugin's routes: its configuration page in the admin area, and the customer's payment page,
/// with what its script and its form ask. The public ones carry the language code when the store's
/// URLs do (the pattern of nopCommerce's BaseRouteProvider, which lives in Nop.Web), and the
/// order's GUID with the secret of its page.
/// </summary>
public partial class RouteProvider : IRouteProvider
{
    /// <summary>Registers the routes.</summary>
    public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
    {
        ArgumentNullException.ThrowIfNull(endpointRouteBuilder);
        var lang = LanguageRoutePattern();
        const string order = "{orderGuid:guid}/{token:length(64)}";

        endpointRouteBuilder.MapControllerRoute(name: SattlemeDefaults.Route.CONFIGURE,
            pattern: "Admin/Sattleme/Configure",
            defaults: new { controller = "SattlemeAdmin", action = "Configure", area = AreaNames.ADMIN });

        endpointRouteBuilder.MapControllerRoute(name: SattlemeDefaults.Route.PAY,
            pattern: $"{lang}/sattleme/pay/{order}",
            defaults: new { controller = "SattlemePayment", action = "Pay" });

        endpointRouteBuilder.MapControllerRoute(name: SattlemeDefaults.Route.STATUS,
            pattern: $"{lang}/sattleme/status/{order}",
            defaults: new { controller = "SattlemePayment", action = "Status" });

        endpointRouteBuilder.MapControllerRoute(name: SattlemeDefaults.Route.NEW_INVOICE,
            pattern: $"{lang}/sattleme/new-invoice/{order}",
            defaults: new { controller = "SattlemePayment", action = "NewInvoice" });
    }

    /// <summary>As nopCommerce's own route providers.</summary>
    public int Priority => 0;

    /// <summary>The language code's part of a public route, when the store's URLs carry one (BaseRouteProvider.GetLanguageRoutePattern).</summary>
    private static string LanguageRoutePattern()
    {
        if (!DataSettingsManager.IsDatabaseInstalled())
            return string.Empty;
        var localizationSettings = EngineContext.Current.Resolve<LocalizationSettings>();
        return localizationSettings.SeoFriendlyUrlsForLanguagesEnabled
            ? $"{{{NopRoutingDefaults.RouteValue.Language}:maxlength(2):{NopRoutingDefaults.LanguageParameterTransformer}=}}"
            : string.Empty;
    }
}