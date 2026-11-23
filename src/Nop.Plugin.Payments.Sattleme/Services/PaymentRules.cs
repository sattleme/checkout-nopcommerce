using System.Globalization;
using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Sattleme.Domain;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;
using PaymentState = NopSattleme.Sattleme.Checkout.PaymentState;
using SattlemeException = NopSattleme.Sattleme.Checkout.SattlemeException;
using SdkPaymentStatus = NopSattleme.Sattleme.Checkout.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>Where an order's Bitcoin payment is, as its customer is told (the payment page).</summary>
public enum PaymentPhase
{
    /// <summary>No invoice is made: the last try failed.</summary>
    None,

    /// <summary>Waiting for the payment.</summary>
    Open,

    /// <summary>Less than the amount came on-chain: the rest may still be paid.</summary>
    Underpaid,

    /// <summary>Its time is over, its final status not known yet.</summary>
    Ended,

    /// <summary>Enough came on-chain, being confirmed.</summary>
    Unconfirmed,

    /// <summary>The SDK reported it paid; nopCommerce's completion of the order is under way.</summary>
    Arrived,

    /// <summary>
    /// The SDK reported it paid, and the order was not marked paid: the merchant looks at it (its
    /// total changed since the invoice was made, it was cancelled, or nopCommerce could not mark it).
    /// </summary>
    Review,

    /// <summary>The order is paid.</summary>
    Paid,

    /// <summary>Never paid, for good (with what came, when something did: underpaid).</summary>
    Expired,

    /// <summary>The order was cancelled.</summary>
    Cancelled,
}

/// <summary>The rules every part of the plugin applies to an order's invoices.</summary>
public static partial class PaymentRules
{
    #region Methods

    /// <summary>
    /// Whether the invoice may still be paid, or a payment of it be confirmed: until its final
    /// status is known, from a check made after its end and its grace period (expired, nothing
    /// received; or paid). Its time being over is not enough: a payment made in its last seconds,
    /// or an on-chain one dated in time, is found by that check. An order has one such invoice at
    /// a time: it gets a new one only once the last one is known to have ended unpaid, and is not
    /// cancelled by its customer meanwhile.
    /// </summary>
    public static bool MayBePaid(SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.InvoiceJson is not null && !invoice.Final;
    }

    /// <summary>
    /// Whether something of the invoice came, as its last status says: paid (in Lightning, whose
    /// status gives no <c>received</c>, or on-chain), or sats received (on-chain, and by invoices of
    /// the rest of an underpayment), whatever became of it since (an underpayment that ended
    /// <c>expired</c> reached the merchant's wallet all the same).
    /// </summary>
    public static bool SomethingCame(SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.InvoiceJson is not null && (invoice.State == "paid" || invoice.Received > 0);
    }

    /// <summary>
    /// Why the customer may not cancel the order of <paramref name="invoices"/> themselves (the
    /// plugin's resource that says it), or null when they may: while a payment may still come (an
    /// invoice that can be paid, or a payment being confirmed), or once something came (the
    /// merchant then settles the order with the customer, never a cancellation that forgets it).
    /// </summary>
    public static string? CancelRefusal(IEnumerable<SattlemeInvoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(invoices);
        var list = invoices.ToList();
        return list.Any(MayBePaid) ? "Cancel.Refused"
            : list.Any(SomethingCame) ? "Cancel.Received"
            : null;
    }

    /// <summary>
    /// What the order still owes: its total (which nopCommerce computes less its gift cards and
    /// reward points: OrderTotalCalculationService.GetShoppingCartTotalAsync), less what was
    /// refunded. In the store's primary currency, as nopCommerce keeps an order's amounts.
    /// </summary>
    public static decimal Owed(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        return Math.Max(0m, order.OrderTotal - order.RefundedAmount);
    }

    /// <summary>What the customer is told of the order's payment, from its last invoice.</summary>
    public static PaymentPhase PhaseOf(Order order, SattlemeInvoice? invoice, long now)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.PaymentStatus is NopPaymentStatus.Paid or NopPaymentStatus.PartiallyRefunded or NopPaymentStatus.Refunded)
            return PaymentPhase.Paid;
        var cancelled = order.OrderStatus == OrderStatus.Cancelled || order.Deleted;
        if (invoice?.InvoiceJson is null)
            return cancelled ? PaymentPhase.Cancelled : PaymentPhase.None;
        return invoice.State switch
        {
            "paid" when invoice.Final => PaymentPhase.Review,
            "paid" => PaymentPhase.Arrived,
            "expired" when invoice.Final => PaymentPhase.Expired,
            "unconfirmed" => PaymentPhase.Unconfirmed,
            _ when cancelled => PaymentPhase.Cancelled,
            _ when invoice.Final || now >= invoice.ExpiresAt => PaymentPhase.Ended,
            _ when invoice.Received > 0 => PaymentPhase.Underpaid,
            _ => PaymentPhase.Open,
        };
    }

    /// <summary>
    /// Whether the order's payment page lets the customer pay, now or soon, after the last invoice
    /// <paramref name="invoice"/>: its invoice to pay (open, or the rest of an underpayment), its
    /// time over and its final status being checked (a new invoice once it ended unpaid), or a new
    /// invoice to get (none made, or the last ended with nothing received). Not once a payment is
    /// being confirmed or arrived, the order is paid or cancelled, nor once an invoice ended with
    /// something received: the merchant settles that order, and no new invoice is made for it.
    /// </summary>
    public static bool PageTakesPayment(Order order, SattlemeInvoice? invoice, long now)
    {
        return PhaseOf(order, invoice, now) switch
        {
            PaymentPhase.None or PaymentPhase.Open or PaymentPhase.Underpaid or PaymentPhase.Ended => true,
            PaymentPhase.Expired => !SomethingCame(invoice!),
            _ => false,
        };
    }

    /// <summary>
    /// When the background checks look at the invoice next, after a check that ended at
    /// <paramref name="now"/>: a minute later while it can be paid, just before the end of its grace
    /// period (an on-chain payment a check lists by then came in time, SPEC.md §31) and just after
    /// (its final status); then five minutes later until it is final, or, after failed checks, one
    /// minute doubled at each failure, up to half an hour. Those waits count from
    /// <paramref name="now"/> for a check of the payment page, and from the start of the scheduled
    /// task's run (<paramref name="run"/>) for one of its checks, less <see
    /// cref="SattlemeDefaults.RunSlack"/>: a run checks what is due when it starts, a check ends
    /// some seconds after its run started, and the next run starts a period after this one, so that
    /// a wait counted from the check's end would always miss the run it was meant for (every
    /// minute would be every two, every five minutes every six).
    /// </summary>
    public static DateTime NextCheck(long expiresAt, int errors, DateTime now, DateTime? run = null)
    {
        var from = run is { } started && started - SattlemeDefaults.RunSlack < now ? started - SattlemeDefaults.RunSlack : now;
        var end = DateTimeOffset.FromUnixTimeSeconds(expiresAt + SattlemeDefaults.GRACE_SECONDS).UtcDateTime;
        DateTime next;
        if (now < end + SattlemeDefaults.EndMargin)
        {
            var mark = now < end - SattlemeDefaults.EndMargin ? end - SattlemeDefaults.EndMargin : end + SattlemeDefaults.EndMargin;
            next = from + SattlemeDefaults.OpenInterval < mark ? from + SattlemeDefaults.OpenInterval : mark;
        }
        else if (errors > 0)
        {
            var backoff = SattlemeDefaults.OpenInterval * Math.Pow(2, Math.Min(errors - 1, 10));
            next = from + (backoff < SattlemeDefaults.MaxBackoff ? backoff : SattlemeDefaults.MaxBackoff);
        }
        else
            next = from + SattlemeDefaults.SettlingInterval;
        return next < now + SattlemeDefaults.PageInterval ? now + SattlemeDefaults.PageInterval : next;
    }

    /// <summary>A short, stable name of an error of a call, for the logs and the record: the SDK's codes (SPEC.md §5), and the plugin's own.</summary>
    public static string ErrorCode(Exception error)
    {
        return error switch
        {
            SattlemeException sdk => sdk.Code,
            OperationCanceledException or TimeoutException => "timeout",
            MissingKeyException => "no_key",
            MismatchException => "mismatch",
            _ => "error",
        };
    }

    /// <summary>
    /// The price the SDK is asked for: <paramref name="total"/> with exactly <paramref name="digits"/>
    /// decimals, rounded up to the currency's smallest unit (the merchant never receives less); null
    /// when it is not above 0.
    /// </summary>
    public static string? PriceAmount(decimal total, int digits)
    {
        if (digits is < 0 or > 4)
            return null;
        var factor = (decimal)Math.Pow(10, digits);
        var units = decimal.Ceiling(total * factor);
        if (units <= 0)
            return null;
        return (units / factor).ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>Whether the invoice was asked for the price <paramref name="amount"/> in <paramref name="currency"/>.</summary>
    public static bool SamePrice(SattlemeInvoice invoice, string amount, string currency)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        return invoice.PriceCurrency == currency
            && decimal.TryParse(invoice.PriceAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var asked)
            && decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var other)
            && asked == other;
    }

    /// <summary>The sats a status says came in time (0 when it says none).</summary>
    public static long ReceivedOf(SdkPaymentStatus status)
    {
        return StoredInvoice.SatsOf(status switch
        {
            SdkPaymentStatus.Open open => open.Received,
            SdkPaymentStatus.Unconfirmed unconfirmed => unconfirmed.Received,
            SdkPaymentStatus.Paid paid => paid.Received,
            SdkPaymentStatus.Expired expired => expired.Received,
            _ => null,
        });
    }

    /// <summary>
    /// What came for a paid invoice of <paramref name="sats"/> sats: in Lightning, the invoice's own
    /// amount (a Lightning invoice is paid at its amount; the SDK's status gives no received for
    /// it); on-chain or mixed, what the status says came in time.
    /// </summary>
    public static long CameOf(SdkPaymentStatus.Paid paid, long sats)
    {
        ArgumentNullException.ThrowIfNull(paid);
        return paid.Method == NopSattleme.Sattleme.Checkout.PaymentMethod.Lightning ? sats : ReceivedOf(paid);
    }

    /// <summary>The state of a status, as the SDK's JSON writes it.</summary>
    public static string StateOf(SdkPaymentStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.State switch
        {
            PaymentState.Open => "open",
            PaymentState.Unconfirmed => "unconfirmed",
            PaymentState.Paid => "paid",
            _ => "expired",
        };
    }

    #endregion
}

/// <summary>No integration key is set for the order's store, or the one saved cannot be read.</summary>
public sealed class MissingKeyException : Exception
{
    /// <summary>No key.</summary>
    public MissingKeyException()
        : base("No sattle.me integration key is set for this store")
    {
    }
}

/// <summary>The invoice the SDK gave is not of the price asked: never shown.</summary>
public sealed class MismatchException : Exception
{
    /// <summary>An invoice of another price.</summary>
    public MismatchException(string message)
        : base(message)
    {
    }
}