using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Payments.Sattleme.Factories;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Orders;
using Nop.Web.Framework.Components;
using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Components;

/// <summary>
/// The order's page in the shop (My account → Orders → Details): where its Bitcoin payment is, and
/// the way back to its payment page. Shown to the order's customer only (nopCommerce's page checks
/// it, and so does this component).
/// </summary>
public partial class OrderPaymentViewComponent : NopViewComponent
{
    #region Fields

    private readonly IOrderService _orderService;
    private readonly IWorkContext _workContext;
    private readonly PaymentModelFactory _factory;
    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The component.</summary>
    public OrderPaymentViewComponent(IOrderService orderService,
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

    /// <summary>The block of the order <paramref name="additionalData"/> (nopCommerce's model of the page), when it is paid with Bitcoin.</summary>
    public async Task<IViewComponentResult> InvokeAsync(string widgetZone, object additionalData)
    {
        if (additionalData is not BaseNopEntityModel { Id: > 0 } page)
            return Content("");
        var order = await _orderService.GetOrderByIdAsync(page.Id);
        var customer = await _workContext.GetCurrentCustomerAsync();
        if (order is null || order.Deleted || order.CustomerId != customer.Id || order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME)
            return Content("");
        var record = await _store.GetOrderAsync(order.OrderGuid);
        if (record is null)
            return Content("");
        var invoices = await _store.GetInvoicesAsync(order.OrderGuid);
        var model = await _factory.PrepareOrderPaymentAsync(record, order, invoices.Count == 0 ? null : invoices[^1]);
        return await ViewAsync(SattlemeDefaults.VIEWS + "OrderPayment.cshtml", model);
    }

    #endregion
}