using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Nop.Core;
using Nop.Core.Domain.Orders;
using Nop.Core.Http;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Models;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Catalog;
using Nop.Services.Directory;
using Nop.Services.Helpers;
using Nop.Services.Localization;
using Nop.Web.Framework.Mvc.Routing;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Factories;

/// <summary>The models of the payment page and of the order pages' widgets.</summary>
public partial class PaymentModelFactory
{
    #region Fields

    private static readonly Regex _txid = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex _address = new("^[a-z0-9]{14,90}$", RegexOptions.CultureInvariant);

    private readonly ICurrencyService _currencyService;
    private readonly IDateTimeHelper _dateTimeHelper;
    private readonly ILocalizationService _localizationService;
    private readonly INopUrlHelper _nopUrlHelper;
    private readonly IPriceFormatter _priceFormatter;
    private readonly IWorkContext _workContext;
    private readonly TimeProvider _time;

    #endregion

    #region Ctor

    /// <summary>The factory.</summary>
    public PaymentModelFactory(ICurrencyService currencyService,
        IDateTimeHelper dateTimeHelper,
        ILocalizationService localizationService,
        INopUrlHelper nopUrlHelper,
        IPriceFormatter priceFormatter,
        IWorkContext workContext,
        TimeProvider time)
    {
        _currencyService = currencyService;
        _dateTimeHelper = dateTimeHelper;
        _localizationService = localizationService;
        _nopUrlHelper = nopUrlHelper;
        _priceFormatter = priceFormatter;
        _workContext = workContext;
        _time = time;
    }

    #endregion

    #region Methods

    /// <summary>The payment page of <paramref name="order"/>, whose last invoice is <paramref name="invoice"/> (null: none made).</summary>
    public virtual async Task<PaymentPageModel> PreparePaymentPageAsync(SattlemeOrder record, Order order, SattlemeInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var phase = PaymentRules.PhaseOf(order, invoice, now);
        var stored = invoice is null ? null : StoredInvoice.Of(invoice);
        var routeValues = new { orderGuid = record.OrderGuid, token = record.PageToken };
        var model = new PaymentPageModel
        {
            Phase = PhaseName(phase),
            OrderNumber = order.CustomOrderNumber,
            OrderUrl = _nopUrlHelper.RouteUrl(NopRouteNames.Standard.ORDER_DETAILS, new { orderId = order.Id }),
            StatusUrl = _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.STATUS, routeValues),
            CheckUrl = _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.PAY, routeValues) + "?check=1",
            NewInvoiceUrl = _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.NEW_INVOICE, routeValues),
            CanMakeNew = order.PaymentStatus == NopPaymentStatus.Pending && order.OrderStatus != OrderStatus.Cancelled && !order.Deleted
                && (phase == PaymentPhase.None || (phase == PaymentPhase.Expired && invoice!.Received == 0)),
            Version = Version(phase, invoice, stored),
            PollSeconds = phase switch
            {
                PaymentPhase.Open or PaymentPhase.Underpaid or PaymentPhase.Arrived => 3,
                PaymentPhase.Ended => 5,
                PaymentPhase.Unconfirmed => 30,
                _ => null,
            },
        };
        if (stored is null)
            return model;

        var underpaid = phase == PaymentPhase.Underpaid;
        var toPay = underpaid ? stored.RestSats ?? 0 : stored.Sats;
        var uri = underpaid ? stored.RestPaymentUri : stored.PaymentUri;
        var show = phase is PaymentPhase.Open or PaymentPhase.Underpaid;
        uri = uri is not null && (uri.StartsWith("bitcoin:", StringComparison.Ordinal) || uri.StartsWith("lightning:", StringComparison.Ordinal)) ? uri : null;
        model.HasOnchain = stored.OnchainAddress is not null;
        model.Sats = Sats(stored.Sats);
        model.ToPaySats = Sats(toPay);
        model.ToPayBtc = Btc(toPay);
        model.ReceivedSats = Sats(stored.Received);
        model.HasReceived = stored.Received > 0;
        model.Confirmations = stored.Confirmations ?? 0;
        model.Price = await PriceAsync(stored);
        model.SecondsLeft = Math.Max(0, stored.ExpiresAt - now);
        model.TimeLeft = $"{model.SecondsLeft / 60}:{model.SecondsLeft % 60:00}";
        model.Until = await TimeAsync(stored.ExpiresAt, "t");
        if (show && uri is not null)
        {
            model.Uri = uri;
            model.QrSvg = QrSvg.Of(uri, await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + "Pay.QrLabel"));
            model.Lightning = underpaid ? stored.RestBolt11 : stored.Bolt11;
            // On-chain to the invoice's address when its URI says so (a rest below 1 000 sats is Lightning only).
            model.Address = uri.StartsWith("bitcoin:", StringComparison.Ordinal) ? stored.OnchainAddress : null;
        }
        return model;
    }

    /// <summary>What the payment page's script gets: where the payment is, and the page's version.</summary>
    public virtual (string Phase, string Version) PrepareStatus(Order order, SattlemeInvoice? invoice)
    {
        var phase = PaymentRules.PhaseOf(order, invoice, _time.GetUtcNow().ToUnixTimeSeconds());
        return (PhaseName(phase), Version(phase, invoice, invoice is null ? null : StoredInvoice.Of(invoice)));
    }

    /// <summary>What the order's page in the shop says of its payment.</summary>
    public virtual async Task<OrderPaymentModel> PrepareOrderPaymentAsync(SattlemeOrder record, Order order, SattlemeInvoice? invoice)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(order);
        var phase = PaymentRules.PhaseOf(order, invoice, _time.GetUtcNow().ToUnixTimeSeconds());
        var stored = invoice is null ? null : StoredInvoice.Of(invoice);
        // Ended with something received: the block says what the payment page says (what came, of
        // its sats; the merchant settles it), and leads nowhere: the page has nothing more.
        var underpaid = phase == PaymentPhase.Expired && stored is { Received: > 0 };
        return new OrderPaymentModel
        {
            Phase = PhaseName(phase),
            PayUrl = _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.PAY, new { orderGuid = record.OrderGuid, token = record.PageToken }),
            Until = invoice is null ? null : await TimeAsync(invoice.ExpiresAt, "t"),
            HasReceived = underpaid,
            ReceivedSats = Sats(stored?.Received ?? 0),
            Sats = Sats(stored?.Sats ?? 0),
            ShowPayLink = phase is not (PaymentPhase.Paid or PaymentPhase.Cancelled or PaymentPhase.Review) && !underpaid,
        };
    }

    /// <summary>What the order's page in the admin area shows of its invoices, the latest first.</summary>
    public virtual async Task<AdminOrderPaymentModel> PrepareAdminOrderPaymentAsync(Order order, IList<SattlemeInvoice> invoices)
    {
        ArgumentNullException.ThrowIfNull(order);
        ArgumentNullException.ThrowIfNull(invoices);
        var now = _time.GetUtcNow().ToUnixTimeSeconds();
        var model = new AdminOrderPaymentModel();
        foreach (var invoice in invoices.OrderByDescending(invoice => invoice.Id))
            model.Invoices.Add(await PrepareAdminInvoiceAsync(invoice));

        if (invoices.FirstOrDefault(PaymentRules.MayBePaid) is { } payable)
        {
            var end = payable.ExpiresAt + SattlemeDefaults.GRACE_SECONDS;
            // Payable while nothing came: not once paid (in Lightning: Received stays 0), nor while a
            // payment is being confirmed.
            model.Warning = now < end && !PaymentRules.SomethingCame(payable) && payable.State != "unconfirmed"
                ? string.Format(CultureInfo.CurrentCulture, await ResourceAsync("Admin.Warning.Payable"), await TimeAsync(end, "G"))
                : await ResourceAsync("Admin.Warning.Confirming");
        }
        return model;
    }

    #endregion

    #region Utilities

    private async Task<AdminInvoiceModel> PrepareAdminInvoiceAsync(SattlemeInvoice invoice)
    {
        var stored = StoredInvoice.Of(invoice);
        var model = new AdminInvoiceModel
        {
            Reference = invoice.Reference,
            State = await ResourceAsync("State." + (invoice.Error == "abandoned" ? "abandoned" : invoice.InvoiceJson is null ? "none" : invoice.State ?? "unchecked")),
            Created = await TimeAsync(invoice.CreatedOnUtc),
            LastCheck = invoice.CheckedOnUtc is { } checkedOn ? await TimeAsync(checkedOn) : null,
            NextCheck = !invoice.Final && invoice.NextCheckOnUtc is { } next ? await TimeAsync(next) : null,
            Error = string.IsNullOrEmpty(invoice.Error) || invoice.Error == "abandoned" ? null : await ErrorTextAsync(invoice.Error),
            Shredded = invoice.Shredded,
            Amount = $"{invoice.PriceAmount} {invoice.PriceCurrency}",
        };
        if (stored is null)
            return model;

        model.Note = stored.Note;
        model.Amount = $"{Sats(stored.Sats)} sats ({Btc(stored.Sats)} BTC), {stored.FiatAmount ?? invoice.PriceAmount} {stored.FiatCurrency ?? invoice.PriceCurrency}";
        if (stored.RatePrice is not null)
        {
            model.Rate = string.Format(CultureInfo.CurrentCulture, await ResourceAsync("Admin.RateValue"),
                stored.RatePrice, stored.FiatCurrency, stored.RateSource,
                stored.RateFetchedAt is { } fetched ? await TimeAsync(fetched, "G") : "");
        }
        model.Expires = await TimeAsync(stored.ExpiresAt, "G");
        model.Received = stored.State is null ? null : await ReceivedAsync(stored);
        model.Confirmations = stored.Confirmations;
        model.Address = stored.OnchainAddress;
        model.AddressLink = stored.OnchainAddress is { } address && _address.IsMatch(address);
        model.Txids = [.. stored.Txids.Where(txid => _txid.IsMatch(txid))];
        model.Preimages = [.. stored.Preimages];
        return model;
    }

    /// <summary>
    /// What came for the admin: in sats, and how once paid (in Lightning: its Lightning invoice's
    /// amount, which the SDK's status does not repeat; on-chain; or both, in all).
    /// </summary>
    private async Task<string> ReceivedAsync(StoredInvoice stored)
    {
        var resource = stored.State != "paid" ? null : stored.Method switch
        {
            "lightning" => "Admin.ReceivedLightning",
            "onchain" => "Admin.ReceivedOnchain",
            "mixed" => "Admin.ReceivedMixed",
            _ => null,
        };
        return resource is null
            ? $"{Sats(stored.Came)} sats"
            : string.Format(CultureInfo.CurrentCulture, await ResourceAsync(resource), Sats(stored.Came));
    }

    private Task<string> ResourceAsync(string name)
    {
        return _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + name);
    }

    /// <summary>An error's code in plain words (the code itself when it is not known).</summary>
    public virtual async Task<string> ErrorTextAsync(string code)
    {
        var language = await _workContext.GetWorkingLanguageAsync();
        var text = await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + "ErrorCode." + code, language.Id,
            logIfNotFound: false, defaultValue: "", returnEmptyIfNotFound: true);
        return string.IsNullOrEmpty(text) ? code : text;
    }

    private async Task<string?> PriceAsync(StoredInvoice stored)
    {
        if (stored.FiatAmount is null || stored.FiatCurrency is null
            || !decimal.TryParse(stored.FiatAmount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount))
            return null;
        var currency = await _currencyService.GetCurrencyByCodeAsync(stored.FiatCurrency);
        return currency is null
            ? $"{stored.FiatAmount} {stored.FiatCurrency}"
            : await _priceFormatter.FormatPriceAsync(amount, true, currency);
    }

    private async Task<string> TimeAsync(long unixSeconds, string format)
    {
        return (await _dateTimeHelper.ConvertToUserTimeAsync(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime, DateTimeKind.Utc))
            .ToString(format, CultureInfo.CurrentCulture);
    }

    private async Task<string> TimeAsync(DateTime utc)
    {
        return (await _dateTimeHelper.ConvertToUserTimeAsync(DateTime.SpecifyKind(utc, DateTimeKind.Utc), DateTimeKind.Utc))
            .ToString("G", CultureInfo.CurrentCulture);
    }

    private static string PhaseName(PaymentPhase phase)
    {
        return phase.ToString().ToLowerInvariant();
    }

    /// <summary>An amount in sats, as the customer's language writes numbers.</summary>
    private static string Sats(long sats)
    {
        return sats.ToString("N0", CultureInfo.CurrentCulture);
    }

    /// <summary>An amount in sats, in bitcoins, exactly: 49 000 is 0.00049.</summary>
    public static string Btc(long sats)
    {
        return (sats / 100_000_000m).ToString("0.########", CultureInfo.InvariantCulture);
    }

    /// <summary>What the page shows, in a few characters: its script reloads it when this changes.</summary>
    private static string Version(PaymentPhase phase, SattlemeInvoice? invoice, StoredInvoice? stored)
    {
        var shown = string.Join('|', phase, invoice?.Id, stored?.Received, stored?.RestBolt11, stored?.RestPaymentUri, stored?.Confirmations);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(shown)))[..16];
    }

    #endregion
}