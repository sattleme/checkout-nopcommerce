using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Nop.Core.Http;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Localization;
using Nop.Services.Messages;
using Nop.Services.Orders;

namespace Nop.Plugin.Payments.Sattleme.Infrastructure;

/// <summary>
/// Keeps a customer from cancelling their order (nopCommerce's "Cancel order" on the order's page,
/// OrderController.CancelOrder, offered by default to orders pending payment) while a Bitcoin
/// payment may still come for it (its invoice can still be paid, or what came is being confirmed),
/// or once something came (paid, or an underpayment that reached the merchant's wallet: the
/// merchant then settles the order), whatever a theme's order page shows (OrderDetailsConsumer
/// hides the button too). Once every invoice ended with nothing received, the order can be
/// cancelled. The admin may cancel any order: the order's page then says what a payment that
/// still comes needs (README.md, "Payments and orders").
/// </summary>
public partial class CancelOrderFilter : IAsyncActionFilter
{
    #region Fields

    /// <summary>nopCommerce's controller of the customer's orders, and its action.</summary>
    public const string CONTROLLER = "Nop.Web.Controllers.OrderController";

    /// <inheritdoc cref="CONTROLLER"/>
    public const string ACTION = "CancelOrder";

    private readonly ILocalizationService _localizationService;
    private readonly INotificationService _notificationService;
    private readonly IOrderService _orderService;
    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The filter.</summary>
    public CancelOrderFilter(ILocalizationService localizationService,
        INotificationService notificationService,
        IOrderService orderService,
        SattlemeStore store)
    {
        _localizationService = localizationService;
        _notificationService = notificationService;
        _orderService = orderService;
        _store = store;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (context.ActionDescriptor is ControllerActionDescriptor { ActionName: ACTION } action
            && action.ControllerTypeInfo.FullName == CONTROLLER
            && context.ActionArguments.TryGetValue("orderId", out var value) && value is int orderId
            && await RefusalAsync(orderId) is { } refusal)
        {
            _notificationService.ErrorNotification(await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + refusal));
            context.Result = new RedirectToRouteResult(NopRouteNames.Standard.ORDER_DETAILS, new { orderId });
            return;
        }
        await next();
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Why the customer may not cancel the order <paramref name="orderId"/> (the plugin's resource
    /// that says it: a Bitcoin payment may still come for it, or something came), or null when they
    /// may (PaymentRules.CancelRefusal).
    /// </summary>
    public virtual async Task<string?> RefusalAsync(int orderId)
    {
        var order = await _orderService.GetOrderByIdAsync(orderId);
        if (order is null || order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME)
            return null;
        return PaymentRules.CancelRefusal(await _store.GetInvoicesAsync(order.OrderGuid));
    }

    #endregion
}