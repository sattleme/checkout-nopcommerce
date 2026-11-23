using Nop.Core.Domain.Orders;
using Nop.Services.Events;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// Notes on a placed order the invoices made for it (README.md, "Payments and orders": the order's
/// notes log the invoices made). Its first invoice is made while the order is being placed, before
/// nopCommerce stores it (ProcessPaymentAsync), so that it has no order to note it on yet: nopCommerce
/// publishes OrderPlacedEvent once the order is stored (OrderProcessingService.PlaceOrderAsync, after
/// its "Order placed" note), and its invoices are noted then. A later invoice, made for the placed
/// order from its payment page, is noted when it is made (InvoiceMaker).
/// </summary>
public partial class OrderPlacedConsumer : IConsumer<OrderPlacedEvent>
{
    #region Fields

    private readonly OrderNotes _notes;
    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The consumer.</summary>
    public OrderPlacedConsumer(OrderNotes notes, SattlemeStore store)
    {
        _notes = notes;
        _store = store;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public virtual async Task HandleEventAsync(OrderPlacedEvent eventMessage)
    {
        ArgumentNullException.ThrowIfNull(eventMessage);
        var order = eventMessage.Order;
        if (order is null || order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME)
            return;
        // Each made under the order's GUID: usually one; more when nopCommerce placed the order on a
        // GUID it kept from an earlier try (PaymentSettings.RegenerateOrderGuidInterval).
        foreach (var invoice in await _store.GetInvoicesAsync(order.OrderGuid))
            await _notes.AddMadeAsync(order.Id, invoice);
    }

    #endregion
}