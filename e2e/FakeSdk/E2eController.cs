using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Misc.SattlemeE2e;

/// <summary>What the end-to-end test asks of its fake: the invoices made, to pay one, or to underpay one.</summary>
public class E2eController : BasePluginController
{
    private readonly E2eGateway _gateway;

    /// <summary>The controller.</summary>
    public E2eController(E2eGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>The references of the invoices made.</summary>
    [HttpGet]
    public IActionResult References()
    {
        return Json(_gateway.References);
    }

    /// <summary>Pays the invoice of <paramref name="reference"/>.</summary>
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public IActionResult Pay(string reference)
    {
        return _gateway.Pay(reference) ? Ok() : NotFound();
    }

    /// <summary><paramref name="sats"/> of the invoice of <paramref name="reference"/> came on-chain, less than its amount.</summary>
    [HttpPost]
    [IgnoreAntiforgeryToken]
    public IActionResult Underpay(string reference, long sats)
    {
        return _gateway.Underpay(reference, sats) ? Ok() : NotFound();
    }
}

/// <summary>The fake's routes.</summary>
public class RouteProvider : IRouteProvider
{
    /// <inheritdoc/>
    public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
    {
        endpointRouteBuilder.MapControllerRoute("Plugin.Misc.SattlemeE2e.References", "e2e-sattleme/references",
            new { controller = "E2e", action = "References" });
        endpointRouteBuilder.MapControllerRoute("Plugin.Misc.SattlemeE2e.Pay", "e2e-sattleme/pay/{reference}",
            new { controller = "E2e", action = "Pay" });
        endpointRouteBuilder.MapControllerRoute("Plugin.Misc.SattlemeE2e.Underpay", "e2e-sattleme/underpay/{reference}/{sats}",
            new { controller = "E2e", action = "Underpay" });
    }

    /// <inheritdoc/>
    public int Priority => 0;
}