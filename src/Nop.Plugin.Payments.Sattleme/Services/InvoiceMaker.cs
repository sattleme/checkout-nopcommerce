using System.Globalization;
using System.Text;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Logging;
using Nop.Services.Payments;
using Nop.Services.Stores;
using NopSattleme.Sattleme.Checkout;
using NopOrder = Nop.Core.Domain.Orders.Order;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;
using SdkOrder = NopSattleme.Sattleme.Checkout.Order;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>The price an invoice is asked for: the order's total in the store's primary currency.</summary>
/// <param name="Amount">As the SDK takes it: <c>24.50</c>.</param>
/// <param name="Currency">ISO 4217, in the SDK's table of currencies.</param>
public sealed record PriceToPay(string Amount, string Currency);

/// <summary>An invoice made (or an earlier one of the order handed out again), or why none is.</summary>
public sealed class MakeResult
{
    private MakeResult(SattlemeInvoice? invoice, bool paid, string? refusal, string? errorCode, object[] arguments)
    {
        Invoice = invoice;
        Paid = paid;
        Refusal = refusal;
        ErrorCode = errorCode;
        RefusalArguments = arguments;
    }

    /// <summary>The order's invoice that may be paid, when one is made.</summary>
    public SattlemeInvoice? Invoice { get; }

    /// <summary>The SDK reported its reference paid already (an earlier try made it, and it was paid).</summary>
    public bool Paid { get; }

    /// <summary>When none is made: the plugin's resource that tells the customer why (a composite format).</summary>
    public string? Refusal { get; }

    /// <summary>The arguments of <see cref="Refusal"/> (a DateTime is in UTC: shown in the customer's time zone).</summary>
    public object[] RefusalArguments { get; }

    /// <summary>When none is made because a call failed: its code (SPEC.md §5, or the plugin's own).</summary>
    public string? ErrorCode { get; }

    /// <summary>An invoice made.</summary>
    public static MakeResult Made(SattlemeInvoice invoice, bool paid)
    {
        return new(invoice, paid, null, null, []);
    }

    /// <summary>No invoice, for the reason <paramref name="refusal"/>.</summary>
    public static MakeResult Refused(string refusal, string? errorCode = null, params object[] arguments)
    {
        return new(null, false, refusal, errorCode, arguments);
    }
}

/// <summary>
/// Makes the invoices of orders (README.md, "Payments and orders"). The first one when the customer
/// places the order (nopCommerce's ProcessPaymentAsync, before it stores the order: a refusal then
/// lets the customer choose another payment method), a new one from the payment page once the last
/// one ended unpaid. For each:
/// <list type="bullet">
/// <item>a record with a new random reference (128 bits of the CSPRNG), stored before the SDK is
/// asked for an invoice of it; a try that follows a failed one asks for the same reference, and the
/// SDK hands out the invoice it may have made meanwhile, or reports it paid;</item>
/// <item>the order locked in the database around the SDK's call (SattlemeStore.TryLockOrderAsync):
/// two requests at once (a double click, two servers) get one invoice;</item>
/// <item>one invoice that may be paid per order: a new one only once the last ended unpaid.</item>
/// </list>
/// </summary>
public partial class InvoiceMaker
{
    #region Fields

    /// <summary>How often a request waiting for the order's lock tries to take it.</summary>
    public static readonly TimeSpan LockPoll = TimeSpan.FromMilliseconds(250);

    private readonly CurrencySettings _currencySettings;
    private readonly ICurrencyService _currencyService;
    private readonly ILogger _logger;
    private readonly IntegrationKeys _keys;
    private readonly ISattlemeGateway _gateway;
    private readonly ISettingService _settingService;
    private readonly IStoreService _storeService;
    private readonly OrderNotes _notes;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    #endregion

    #region Ctor

    /// <summary>The maker of invoices.</summary>
    public InvoiceMaker(CurrencySettings currencySettings,
        ICurrencyService currencyService,
        ILogger logger,
        IntegrationKeys keys,
        ISattlemeGateway gateway,
        ISettingService settingService,
        IStoreService storeService,
        OrderNotes notes,
        SattlemeStore store,
        TimeProvider time)
    {
        _currencySettings = currencySettings;
        _currencyService = currencyService;
        _logger = logger;
        _keys = keys;
        _gateway = gateway;
        _settingService = settingService;
        _storeService = storeService;
        _notes = notes;
        _store = store;
        _time = time;
    }

    #endregion

    #region Methods

    /// <summary>
    /// The invoice of an order being placed (<paramref name="request"/>, nopCommerce's
    /// ProcessPaymentAsync): made before nopCommerce stores the order, under its GUID.
    /// </summary>
    public virtual async Task<MakeResult> MakeForPlacementAsync(ProcessPaymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.OrderGuid == Guid.Empty)
            return MakeResult.Refused("Error.Unavailable", "error");
        var price = await PriceOfAsync(request.OrderTotal);
        if (price is null)
            return MakeResult.Refused("Error.NotPayable", "currency");
        var record = await _store.GetOrMakeOrderAsync(request.OrderGuid, request.StoreId, request.CustomerId, UtcNow());
        return await MakeAsync(record, price, null);
    }

    /// <summary>
    /// A new invoice for the placed order <paramref name="order"/>, whose last invoice ended unpaid
    /// (or made none): the payment page's "new invoice".
    /// </summary>
    public virtual async Task<MakeResult> MakeAgainAsync(NopOrder order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.Deleted || order.OrderStatus == OrderStatus.Cancelled || order.PaymentStatus != NopPaymentStatus.Pending
            || order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME)
            return MakeResult.Refused("Error.NotPending");
        var price = await PriceOfAsync(PaymentRules.Owed(order));
        if (price is null)
            return MakeResult.Refused("Error.NotPayable", "currency");
        var record = await _store.GetOrMakeOrderAsync(order.OrderGuid, order.StoreId, order.CustomerId, UtcNow());
        await _store.SetOrderIdAsync(order.OrderGuid, order.Id);
        return await MakeAsync(record, price, order);
    }

    /// <summary>
    /// The price of <paramref name="total"/> (in the store's primary currency), as the SDK takes it;
    /// null when that currency is not one the SDK converts, or the total is not above 0.
    /// </summary>
    public virtual async Task<PriceToPay?> PriceOfAsync(decimal total)
    {
        var currency = await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId);
        var code = currency?.CurrencyCode;
        if (code is null || Currencies.DigitsOf(code) is not { } digits)
            return null;
        return PaymentRules.PriceAmount(total, digits) is { } amount ? new PriceToPay(amount, code) : null;
    }

    #endregion

    #region Utilities

    private DateTime UtcNow()
    {
        return _time.GetUtcNow().UtcDateTime;
    }

    private async Task<MakeResult> MakeAsync(SattlemeOrder record, PriceToPay price, NopOrder? order)
    {
        var key = await _keys.KeyOfStoreAsync(record.StoreId);
        if (key is null)
        {
            await _logger.WarningAsync("sattle.me: no Bitcoin invoice can be made: no integration key is set for the store, or the one saved cannot be read");
            return MakeResult.Refused("Error.Unavailable", "no_key");
        }

        // The order's lock: another request making its invoice holds it until it is done.
        var lockId = SattlemeStore.NewSecret(16);
        var started = _time.GetTimestamp();
        while (true)
        {
            var now = UtcNow();
            if (await _store.TryLockOrderAsync(record.OrderGuid, lockId, now, now + SattlemeDefaults.CreateTimeout + SattlemeDefaults.LeaseMargin))
                break;
            if (_time.GetElapsedTime(started) > SattlemeDefaults.CreateTimeout + SattlemeDefaults.LockWait)
                return MakeResult.Refused("Error.TryAgain", "busy");
            await Task.Delay(LockPoll, _time);
        }
        var waited = _time.GetElapsedTime(started);

        try
        {
            return await MakeLockedAsync(record, price, order, key, waited);
        }
        finally
        {
            await _store.UnlockOrderAsync(record.OrderGuid, lockId);
        }
    }

    private async Task<MakeResult> MakeLockedAsync(SattlemeOrder record, PriceToPay price, NopOrder? order, string key, TimeSpan waited)
    {
        var invoices = await _store.GetInvoicesAsync(record.OrderGuid);
        var now = UtcNow();
        var unixNow = _time.GetUtcNow().ToUnixTimeSeconds();

        // One invoice that may be paid per order: that one (its page shows where it is), never a
        // second one.
        if (invoices.FirstOrDefault(PaymentRules.MayBePaid) is { } payable)
        {
            return PaymentRules.SamePrice(payable, price.Amount, price.Currency)
                ? MakeResult.Made(payable, payable.State == "paid")
                : Busy(payable, unixNow);
        }

        var last = invoices.LastOrDefault();
        if (last is { Final: true, State: "paid" })
            return MakeResult.Refused("Error.NotPending");
        if (last is { Final: true, Received: > 0 })
            return MakeResult.Refused("Error.Underpaid");

        // Waited long for another request that made no invoice: two calls in a row could outlast this one.
        if (waited > SattlemeDefaults.LockWait)
            return MakeResult.Refused("Error.TryAgain", "busy");

        // A try that made no invoice (a timeout, an error) asks for the same reference again.
        SattlemeInvoice attempt;
        if (last is { InvoiceJson: null, Final: false } && PaymentRules.SamePrice(last, price.Amount, price.Currency))
            attempt = last;
        else
        {
            attempt = new SattlemeInvoice
            {
                OrderGuid = record.OrderGuid,
                Reference = SattlemeDefaults.REFERENCE_PREFIX + SattlemeStore.NewSecret(16),
                PriceAmount = price.Amount,
                PriceCurrency = price.Currency,
                CreatedOnUtc = now,
            };
            await _store.InsertInvoiceAsync(attempt);
        }

        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(record.StoreId);
        var minutes = Math.Clamp(settings.MinutesToPay, SattlemeDefaults.MIN_MINUTES, SattlemeDefaults.MAX_MINUTES);
        var note = await NoteOfAsync(record.StoreId, record.OrderGuid);

        string? json = null;
        var paid = false;
        Exception? failure = null;
        using (var timeout = new CancellationTokenSource(SattlemeDefaults.CreateTimeout))
        {
            try
            {
                var invoice = await _gateway.ClientOf(key).CreateInvoiceAsync(new SdkOrder
                {
                    Ref = attempt.Reference,
                    Fiat = new Price(price.Amount, price.Currency),
                    DurationSeconds = minutes * 60,
                    Note = note,
                }, timeout.Token);
                json = invoice.ToJson();
            }
            catch (InvoicePendingException pending)
            {
                // An earlier try of this reference failed once the SDK had made its invoice: that one.
                json = pending.Invoice.ToJson();
            }
            catch (OrderPaidException orderPaid)
            {
                // That invoice was paid already (this server lost it): stored, then checked, which
                // marks the order paid as any check does.
                json = orderPaid.Invoice.ToJson();
                paid = true;
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        }

        if (json is not null && Mismatch(json, price) is { } mismatch)
            failure = mismatch;
        if (failure is not null)
            return await RefuseAsync(attempt, order, failure);

        var made = StoredInvoice.Of(new SattlemeInvoice { InvoiceJson = json })!;
        await _store.StoreMadeInvoiceAsync(attempt.Id, json!, made.ExpiresAt, UtcNow());
        var stored = await _store.GetInvoiceAsync(attempt.Id) ?? attempt;
        // The order's first invoice is made before nopCommerce stores the order: its note is added
        // once the order is placed (OrderPlacedConsumer).
        if (order is not null)
            await _notes.AddMadeAsync(order.Id, stored);
        return MakeResult.Made(stored, paid);
    }

    /// <summary>
    /// Why the order cannot get a new invoice while <paramref name="payable"/> may still be paid:
    /// it can be paid until a time; its time is over and its final status is being checked; or a
    /// payment of it is being confirmed.
    /// </summary>
    private static MakeResult Busy(SattlemeInvoice payable, long now)
    {
        if (payable.Received > 0 || payable.State is "unconfirmed" or "paid")
            return MakeResult.Refused("Error.Confirming");
        var end = payable.ExpiresAt + SattlemeDefaults.GRACE_SECONDS;
        return now < end
            ? MakeResult.Refused("Error.Pending", null, DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime)
            : MakeResult.Refused("Error.Checking");
    }

    /// <summary>Why the invoice <paramref name="json"/> must not be shown: it is not of the price asked.</summary>
    private static MismatchException? Mismatch(string json, PriceToPay price)
    {
        var made = StoredInvoice.Of(new SattlemeInvoice { InvoiceJson = json });
        var same = made is { FiatAmount: { } amount, FiatCurrency: { } currency }
            && currency == price.Currency
            && decimal.TryParse(amount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var asked)
            && asked == decimal.Parse(price.Amount, CultureInfo.InvariantCulture);
        return same ? null : new MismatchException($"an invoice of {made?.FiatAmount} {made?.FiatCurrency}, for a price of {price.Amount} {price.Currency}");
    }

    /// <summary>Logs why no invoice was made (never the key: the SDK's messages hold none), then tells the customer.</summary>
    private async Task<MakeResult> RefuseAsync(SattlemeInvoice attempt, NopOrder? order, Exception failure)
    {
        var code = PaymentRules.ErrorCode(failure);
        await _store.SetMakeErrorAsync(attempt.Id, code);
        var transient = failure is SattlemeException { Retryable: true } || code == "timeout";
        var ofOrder = failure is InvalidOrderException or UnsupportedCurrencyException;
        var message = $"sattle.me: no Bitcoin invoice for the reference {attempt.Reference}: {code}: {failure.Message}";
        if (transient || ofOrder)
            await _logger.WarningAsync(message);
        else
            await _logger.ErrorAsync(message, code == "error" ? failure : null);
        if (order is not null)
            await _notes.AddAsync(order.Id, "Note.Failed", attempt.Reference, new NoteResource("ErrorCode." + code));
        return MakeResult.Refused(transient ? "Error.TryAgain" : ofOrder ? "Error.NotPayable" : "Error.Unavailable", code);
    }

    /// <summary>
    /// What the merchant's app shows of the sale: the store's name and the first characters of the
    /// order's GUID (the order has no number yet when its first invoice is made), 100 characters at
    /// most, no control character (the SDK refuses them).
    /// </summary>
    private async Task<string> NoteOfAsync(int storeId, Guid orderGuid)
    {
        var store = await _storeService.GetStoreByIdAsync(storeId);
        var tag = orderGuid.ToString("N")[..8];
        var runes = (store?.Name ?? "").EnumerateRunes()
            .Select(rune => Rune.IsControl(rune) || Rune.IsWhiteSpace(rune) ? new Rune(' ') : rune)
            .ToList();
        var words = string.Concat(runes.Select(rune => rune.ToString())).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var name = string.Join(' ', words).EnumerateRunes().ToList();
        // In code points, as the SDK counts them: the name, " · " and the tag.
        var room = 100 - 3 - tag.Length;
        if (name.Count > room)
            name = [.. name.Take(room - 1), new Rune('…')];
        var cleaned = string.Concat(name.Select(rune => rune.ToString())).Trim();
        return cleaned.Length == 0 ? tag : $"{cleaned} · {tag}";
    }

    #endregion
}