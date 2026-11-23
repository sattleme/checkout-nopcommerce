using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Payments.Sattleme.Factories;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Orders;
using Nop.Web.Framework.Components;
using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Components;

/// <summary>
/// The order's page in the admin area: a block with its Bitcoin invoices (reference, amount and
/// rate, address, transactions, proofs of payment, checks, errors in plain words). Not shown to a
/// vendor, as nopCommerce hides the order's notes from vendors.
/// </summary>
public partial class AdminOrderViewComponent : NopViewComponent
{
    #region Fields

    private readonly IOrderService _orderService;
    private readonly IWorkContext _workContext;
    private readonly PaymentModelFactory _factory;
    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The component.</summary>
    public AdminOrderViewComponent(IOrderService orderService,
        IWorkContext workContext,
        PaymentModelFactory factory,
        SattlemeStore store)
    {
        _orderService = orderService;
        _workContext = workContext;
        _factory = factory;
        _store = store;
    }

    #endregion

    #region Methods

    /// <summary>The block of the order <paramref name="additionalData"/> (the admin area's model of the order), when it has Bitcoin invoices.</summary>
    public async Task<IViewComponentResult> InvokeAsync(string widgetZone, object additionalData)
    {
        if (additionalData is not BaseNopEntityModel { Id: > 0 } page || await _workContext.GetCurrentVendorAsync() is not null)
            return Content("");
        var order = await _orderService.GetOrderByIdAsync(page.Id);
        if (order is null)
            return Content("");
        var invoices = await _store.GetInvoicesAsync(order.OrderGuid);
        if (invoices.Count == 0)
            return Content("");
        var model = await _factory.PrepareAdminOrderPaymentAsync(order, invoices);
        return await ViewAsync(SattlemeDefaults.VIEWS + "AdminOrder.cshtml", model);
    }

    #endregion
}