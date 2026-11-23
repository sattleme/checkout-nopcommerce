using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Events;
using Nop.Services.Orders;
using Nop.Web.Framework.Events;
using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Infrastructure;

/// <summary>
/// Hides "Cancel order" on the customer's page of an order (My account → Orders → Details) whose
/// cancellation CancelOrderFilter refuses: while a Bitcoin payment may still come for it, or once
/// something came. nopCommerce prepares that page's model (OrderModelFactory: CanCancelOrder, true
/// for any order pending payment when customers may cancel orders), then publishes it before the
/// view is rendered (PublishModelEventsAttribute, on every controller of the shop): its
/// ModelPreparedEvent is the way nopCommerce gives plugins to change a page's model. The model is
/// Nop.Web's, which a plugin built against Nop.Web.Framework does not reference: it is known by its
/// type's name, and its property by its name and type, as CancelOrderFilter knows the controller.
/// The filter still refuses the cancellation whatever the page shows (a theme's own view, a link).
/// </summary>
public partial class OrderDetailsConsumer : IConsumer<ModelPreparedEvent<BaseNopModel>>
{
    #region Fields

    /// <summary>nopCommerce's model of the customer's page of an order, and its property.</summary>
    public const string MODEL = "Nop.Web.Models.Order.OrderDetailsModel";

    /// <inheritdoc cref="MODEL"/>
    public const string CAN_CANCEL = "CanCancelOrder";

    private readonly IOrderService _orderService;
    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The consumer.</summary>
    public OrderDetailsConsumer(IOrderService orderService, SattlemeStore store)
    {
        _orderService = orderService;
        _store = store;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public virtual async Task HandleEventAsync(ModelPreparedEvent<BaseNopModel> eventMessage)
    {
        ArgumentNullException.ThrowIfNull(eventMessage);
        // Every model of the shop comes here: only the order's page goes further.
        if (eventMessage.Model is not BaseNopEntityModel { Id: > 0 } model || model.GetType().FullName != MODEL)
            return;
        var canCancel = model.GetType().GetProperty(CAN_CANCEL, typeof(bool));
        if (canCancel is not { CanRead: true, CanWrite: true } || canCancel.GetValue(model) is not true)
            return;

        var order = await _orderService.GetOrderByIdAsync(model.Id);
        if (order is null || order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME)
            return;
        if (PaymentRules.CancelRefusal(await _store.GetInvoicesAsync(order.OrderGuid)) is not null)
            canCancel.SetValue(model, false);
    }

    #endregion
}