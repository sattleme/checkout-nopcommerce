using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Services.Logging;
using Nop.Services.Orders;
using NopSattleme.Sattleme.Checkout;
using NopOrder = Nop.Core.Domain.Orders.Order;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;
using SdkPaymentStatus = NopSattleme.Sattleme.Checkout.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// Checks the payment of an invoice, and applies what the SDK found to its order (README.md,
/// "Payments and orders"):
/// <list type="bullet">
/// <item>only a <c>paid</c> status of the SDK's own check of the invoice the order stored marks the
/// order paid, never the browser; exactly once (the check's lease and the completion's claim are
/// conditional updates), through nopCommerce's standard path (CanMarkOrderAsPaid, then
/// MarkOrderAsPaidAsync), with the invoice's reference as the capture transaction's id;</item>
/// <item><c>expired</c>: never paid, for good; with something received (an on-chain underpayment),
/// a note says what reached the wallet, to settle by hand;</item>
/// <item><c>unconfirmed</c>: waits (3 confirmations, about 30 minutes), with a note once.</item>
/// </list>
/// Two checks of one invoice never run at once, and checks in a row are harmless: the SDK's checks
/// are made to be repeated, and settling is idempotent.
/// </summary>
public partial class PaymentChecker
{
    #region Fields

    /// <summary>
    /// An invoice made when its order was placed, whose order nopCommerce has not stored after this
    /// long, belongs to no order (the placement failed after it was made).
    /// </summary>
    public static readonly TimeSpan PlacementWait = TimeSpan.FromMinutes(10);

    private readonly ILogger _logger;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly IOrderProcessingService _orderProcessingService;
    private readonly IOrderService _orderService;
    private readonly ISattlemeGateway _gateway;
    private readonly OrderNotes _notes;
    private readonly PersonalDataShredder _shredder;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    #endregion

    #region Ctor

    /// <summary>The checker.</summary>
    public PaymentChecker(ILogger logger,
        IntegrationKeys keys,
        InvoiceMaker maker,
        IOrderProcessingService orderProcessingService,
        IOrderService orderService,
        ISattlemeGateway gateway,
        OrderNotes notes,
        PersonalDataShredder shredder,
        SattlemeStore store,
        TimeProvider time)
    {
        _logger = logger;
        _keys = keys;
        _maker = maker;
        _orderProcessingService = orderProcessingService;
        _orderService = orderService;
        _gateway = gateway;
        _notes = notes;
        _shredder = shredder;
        _store = store;
        _time = time;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Checks the payment of the invoice <paramref name="invoiceId"/> now, the SDK's call bounded by
    /// <paramref name="timeout"/>, and applies what was found to its order. Skipped (false) when the
    /// invoice is final or not made, when another check of it is under way, or when one ended less
    /// than <paramref name="minInterval"/> ago. <paramref name="run"/>: when the scheduled task's run
    /// that asks it started (its next check counts from then: PaymentRules.NextCheck), null for the
    /// payment page.
    /// </summary>
    public virtual async Task<bool> CheckAsync(int invoiceId, TimeSpan minInterval, TimeSpan timeout, DateTime? run = null)
    {
        var now = UtcNow();
        var leaseId = SattlemeStore.NewSecret(16);
        DateTime? checkedBefore = minInterval > TimeSpan.Zero ? now - minInterval : null;
        if (!await _store.TryLeaseCheckAsync(invoiceId, leaseId, now, now + timeout + SattlemeDefaults.LeaseMargin, checkedBefore))
            return false;
        var invoice = await _store.GetInvoiceAsync(invoiceId);
        if (invoice?.InvoiceJson is null)
            return false;

        SdkPaymentStatus status;
        try
        {
            var record = await _store.GetOrderAsync(invoice.OrderGuid);
            var key = (record is null ? null : await _keys.KeyOfStoreAsync(record.StoreId)) ?? throw new MissingKeyException();
            using var cancel = new CancellationTokenSource(timeout);
            status = await _gateway.ClientOf(key).GetStatusAsync(InvoiceToCheck.FromJson(invoice.InvoiceJson), cancel.Token);
        }
        catch (Exception exception)
        {
            await FailAsync(invoice, leaseId, exception, PaymentRules.ErrorCode(exception), run);
            return true;
        }

        try
        {
            await SettleAsync(invoice, leaseId, status, run);
        }
        catch (Exception exception)
        {
            // nopCommerce could not take the update (its database, a plugin reacting to the
            // payment): the invoice is not final, and a later check applies its status again.
            await FailAsync(invoice, leaseId, exception, "apply", run);
        }
        return true;
    }

    #endregion

    #region Utilities

    private DateTime UtcNow()
    {
        return _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>A check failed: counted, its next one later, logged when it starts failing or fails otherwise.</summary>
    private async Task FailAsync(SattlemeInvoice invoice, string leaseId, Exception exception, string code, DateTime? run)
    {
        var now = UtcNow();
        await _store.FailCheckAsync(invoice.Id, leaseId, code, now);
        await _store.SetNextCheckAsync(invoice.Id, PaymentRules.NextCheck(invoice.ExpiresAt, invoice.Errors + 1, now, run));
        if (code == invoice.Error)
            return;
        if (code is "error" or "apply")
            await _logger.ErrorAsync($"sattle.me: the check of the Bitcoin invoice {invoice.Reference} failed: {code}", exception);
        else
            await _logger.WarningAsync($"sattle.me: the check of the Bitcoin invoice {invoice.Reference} failed: {code}: {exception.Message}");
    }

    /// <summary>
    /// Stores what the check found and updates the order: paid when paid, its invoice final when
    /// paid or expired. Final only once the order was updated, so that a check that stops half-way
    /// is simply done again.
    /// </summary>
    private async Task SettleAsync(SattlemeInvoice invoice, string leaseId, SdkPaymentStatus status, DateTime? run)
    {
        var previous = invoice.State;
        var received = PaymentRules.ReceivedOf(status);
        if (!await _store.StoreStatusAsync(invoice.Id, leaseId, status.ToJson(), PaymentRules.StateOf(status), received, UtcNow()))
            return; // This check outlived its lease: another one decides.

        var order = await _orderService.GetOrderByGuidAsync(invoice.OrderGuid);
        var final = false;
        switch (status)
        {
            case SdkPaymentStatus.Paid paid:
                final = await CompleteAsync(invoice, leaseId, order, paid);
                break;
            // Final only from a check made after the end of its grace period (the SDK reports expired
            // only then): until then, a payment of its last seconds may still be found.
            case SdkPaymentStatus.Expired when _time.GetUtcNow().ToUnixTimeSeconds() >= invoice.ExpiresAt + SattlemeDefaults.GRACE_SECONDS:
                final = await ExpireAsync(invoice, order, received);
                break;
            // What came, against the invoice's amount. The status does not say how much of it came in
            // Lightning (the rest of an underpayment, when one was paid): the note says so.
            case SdkPaymentStatus.Unconfirmed unconfirmed when previous != "unconfirmed" && order is not null:
                await _notes.AddAsync(order.Id, "Note.Unconfirmed", invoice.Reference, received, StoredInvoice.Of(invoice)?.Sats ?? 0, unconfirmed.Confirmations);
                break;
        }

        // Released only now: no other check settles the order meanwhile. Never made non-final.
        await _store.ReleaseCheckAsync(invoice.Id, leaseId, final, PaymentRules.NextCheck(invoice.ExpiresAt, 0, UtcNow(), run));
        if (final)
            await _shredder.ShredIfFinalAsync(invoice.Id);
    }

    /// <summary>
    /// The invoice was paid: its order is marked paid, once. Whether the invoice is final (false:
    /// checked again, the completion not done yet).
    /// </summary>
    private async Task<bool> CompleteAsync(SattlemeInvoice invoice, string leaseId, NopOrder? order, SdkPaymentStatus.Paid paid)
    {
        var now = UtcNow();
        var sats = StoredInvoice.Of(invoice)?.Sats ?? 0;
        // What came: in Lightning, the invoice's own amount (its status gives no received); on-chain
        // or mixed, what the status says came in time.
        var came = PaymentRules.CameOf(paid, sats);
        if (order is null)
        {
            // Its order is being placed (the invoice is made before nopCommerce stores it): a later
            // check completes it. Long after, it belongs to no order: the bitcoin came all the same.
            if (now - invoice.CreatedOnUtc < PlacementWait)
                return false;
            await _logger.ErrorAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} was paid ({came} sats came), and no order of it was placed: settle it by hand");
            return true;
        }
        if (order.PaymentStatus is NopPaymentStatus.Paid or NopPaymentStatus.PartiallyRefunded or NopPaymentStatus.Refunded)
        {
            // Paid by this invoice (a completion that stopped before its end), or otherwise: then
            // the order was paid twice, which the merchant settles.
            if (order.CaptureTransactionId != invoice.Reference)
            {
                await _notes.AddAsync(order.Id, "Note.PaidTwice", invoice.Reference, came);
                await NoteAmountAsync(order, invoice, paid, sats);
                await _logger.WarningAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} was paid, and the order {order.Id} was paid otherwise: settle it by hand");
            }
            return true;
        }

        // One completion at a time, whichever check sees the payment first: the others wait.
        if (!await _store.ClaimCompletionAsync(invoice.Id, leaseId, now, now - SattlemeDefaults.CompletionStale))
            return false;

        // Whatever the order became, the bitcoin came: the plugin never refuses it, and says what to settle.
        if (order.Deleted || order.OrderStatus == OrderStatus.Cancelled)
        {
            await _notes.AddAsync(order.Id, "Note.PaidCancelled", invoice.Reference, came);
            await NoteAmountAsync(order, invoice, paid, sats);
            await _logger.WarningAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} of the cancelled order {order.Id} was paid: settle it by hand");
            return true;
        }
        // What the order still owes now (amount and currency: its total, less gift cards and reward
        // points, less refunds) against the invoice's price: an admin may edit an order's totals
        // and items in any state (Admin → Orders → Edit). Paid at the invoice's amount, an order
        // that owes another amount is never completed: nopCommerce has no "on hold" state, so its
        // payment stays pending (the orders nopCommerce's admin lists as unpaid), with a note, for
        // the merchant to settle the difference and mark it paid by hand.
        var owed = PaymentRules.Owed(order);
        var price = await _maker.PriceOfAsync(owed);
        if (order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME
            || price is null || !PaymentRules.SamePrice(invoice, price.Amount, price.Currency))
        {
            var owes = price is null
                ? owed.ToString("0.##########", System.Globalization.CultureInfo.InvariantCulture)
                : $"{price.Amount} {price.Currency}";
            await _notes.AddAsync(order.Id, "Note.PaidOtherTotal", invoice.Reference, came, $"{invoice.PriceAmount} {invoice.PriceCurrency}", owes);
            await NoteAmountAsync(order, invoice, paid, sats);
            await _logger.WarningAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} was paid at {invoice.PriceAmount} {invoice.PriceCurrency}, and the order {order.Id} owes {owes} now: its payment stays pending, settle it by hand");
            return true;
        }
        if (!_orderProcessingService.CanMarkOrderAsPaid(order))
        {
            await _notes.AddAsync(order.Id, "Note.PaidNotMarked", invoice.Reference, came);
            await NoteAmountAsync(order, invoice, paid, sats);
            return true;
        }

        order.CaptureTransactionId = invoice.Reference;
        order.CaptureTransactionResult = paid.Method switch
        {
            PaymentMethod.Lightning => "paid: lightning",
            PaymentMethod.Onchain => "paid: on-chain",
            _ => "paid: on-chain and lightning",
        };
        await _orderProcessingService.MarkOrderAsPaidAsync(order);
        // How it was paid and what came, against the invoice's amount; on-chain, how many
        // transactions (never which: a note holds no transaction), and in Lightning by how many
        // invoices of the rest. The status gives no split of what came between the two.
        switch (paid.Method)
        {
            case PaymentMethod.Lightning:
                await _notes.AddAsync(order.Id, "Note.PaidLightning", invoice.Reference, sats);
                break;
            case PaymentMethod.Onchain:
                await _notes.AddAsync(order.Id, "Note.PaidOnchain", invoice.Reference, came, sats, paid.Txids.Count);
                break;
            default:
                await _notes.AddAsync(order.Id, "Note.PaidMixed", invoice.Reference, came, sats, paid.Txids.Count, paid.Preimages.Count);
                break;
        }
        await NoteAmountAsync(order, invoice, paid, sats);
        return true;
    }

    /// <summary>
    /// A payment that received another amount than the invoice's: less, which only the merchant's
    /// tolerance or the merchant's acceptance in its app makes the SDK report paid (on-chain), or
    /// more (an overpayment). A note says by how much. Never a reason to leave the order unpaid:
    /// the SDK's paid status decides that.
    /// </summary>
    private async Task NoteAmountAsync(NopOrder order, SattlemeInvoice invoice, SdkPaymentStatus.Paid paid, long sats)
    {
        var came = PaymentRules.CameOf(paid, sats);
        if (came < sats)
            await _notes.AddAsync(order.Id, "Note.PaidShort", invoice.Reference, sats - came);
        else if (came > sats)
            await _notes.AddAsync(order.Id, "Note.PaidOver", invoice.Reference, came - sats);
    }

    /// <summary>The invoice ended unpaid: final; with something received, a note says what to settle.</summary>
    private async Task<bool> ExpireAsync(SattlemeInvoice invoice, NopOrder? order, long received)
    {
        var sats = StoredInvoice.Of(invoice)?.Sats ?? 0;
        if (received > 0)
        {
            // Some bitcoin came, less than the amount: it reached the merchant's wallet, the order
            // is not paid. The merchant settles it with the customer.
            await _logger.WarningAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} ended underpaid: {received} of {sats} sats");
            if (order is not null)
                await _notes.AddAsync(order.Id, "Note.Underpaid", invoice.Reference, received, sats);
        }
        else if (order is not null)
            await _notes.AddAsync(order.Id, "Note.Expired", invoice.Reference);
        return true;
    }

    #endregion
}