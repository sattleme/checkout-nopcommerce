using Moq;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Factories;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;
using Order = Nop.Core.Domain.Orders.Order;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>What the payment page, the order's page and the admin's block show, in each state.</summary>
[TestFixture]
public sealed class PageModelTests
{
    private Shop _shop = null!;
    private Order _order = null!;
    private SattlemeInvoice _invoice = null!;
    private SattlemeOrder _record = null!;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        _order = _shop.AddOrder();
        _invoice = (await _shop.NewScope().Maker.MakeAgainAsync(_order)).Invoice!;
        _record = _shop.Database.Orders().Single();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private async Task Reports(PaymentStatus status)
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(status);
        _shop.Time.Advance(SattlemeDefaults.PageInterval);
        await _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private Task<Models.PaymentPageModel> PageAsync()
    {
        var invoice = _shop.Database.Invoices().Last();
        return _shop.NewScope().Factory.PreparePaymentPageAsync(_record, _shop.Order(_order.Id), invoice);
    }

    [Test]
    public async Task Open_shows_one_QR_code_the_wallet_link_the_invoice_the_address_the_amounts_and_the_time_left()
    {
        _shop.Time.Advance(TimeSpan.FromSeconds(75));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("open"));
        Assert.That(page.Uri, Is.EqualTo(StoredInvoice.Of(_invoice)!.PaymentUri));
        Assert.That(page.QrSvg, Does.StartWith("<svg").And.EndWith("</svg>"));
        Assert.That(page.Lightning, Is.EqualTo(StoredInvoice.Of(_invoice)!.Bolt11));
        Assert.That(page.Address, Is.EqualTo(SdkData.ADDRESS));
        Assert.That(page.HasOnchain, Is.True);
        Assert.That(page.ToPaySats, Is.EqualTo(24_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.ToPayBtc, Is.EqualTo("0.000245"));
        Assert.That(page.Price, Is.EqualTo("CHF 24.50"));
        Assert.That(page.SecondsLeft, Is.EqualTo(825));
        Assert.That(page.TimeLeft, Is.EqualTo("13:45"));
        Assert.That(page.PollSeconds, Is.EqualTo(3));
        Assert.That(page.OrderNumber, Is.EqualTo(_order.CustomOrderNumber));
    }

    [Test]
    public async Task A_Lightning_only_invoice_shows_no_address()
    {
        _shop.Gateway.Create = (order, _) => Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow, onchain: false));
        var order = _shop.AddOrder(0.50m);
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        var record = _shop.Database.Orders().Single(stored => stored.OrderGuid == order.OrderGuid);
        var page = await _shop.NewScope().Factory.PreparePaymentPageAsync(record, order, invoice);

        Assert.That(page.Uri, Does.StartWith("lightning:"));
        Assert.That(page.Address, Is.Null);
        Assert.That(page.HasOnchain, Is.False);
    }

    [Test]
    public async Task An_underpayment_shows_the_rest_to_pay()
    {
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("underpaid"));
        Assert.That(page.Uri, Is.EqualTo("lightning:lnbc4500n1rest"));
        Assert.That(page.Lightning, Is.EqualTo("lnbc4500n1rest"));
        Assert.That(page.Address, Is.Null, "the rest's URI says how to pay it");
        Assert.That(page.ToPaySats, Is.EqualTo(4_500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.ReceivedSats, Is.EqualTo(20_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
        Assert.That(page.QrSvg, Is.Not.Null);
    }

    [Test]
    public async Task An_underpayment_whose_rest_is_not_ready_says_so()
    {
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500, restReady: false));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("underpaid"));
        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
    }

    [Test]
    public async Task Being_confirmed_shows_no_QR_code_and_asks_less_often()
    {
        await Reports(SdkData.Unconfirmed(24_500, 2));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("unconfirmed"));
        Assert.That(page.Confirmations, Is.EqualTo(2));
        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.PollSeconds, Is.EqualTo(30));
    }

    [Test]
    public async Task Short_of_the_amount_and_accepted_by_the_merchant_the_page_says_being_confirmed()
    {
        // The merchant's acceptance (SPEC.md §31), or its tolerance: received below sats, no rest.
        await Reports(SdkData.Unconfirmed(15_000, 1));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("unconfirmed"));
        Assert.That(page.Uri, Is.Null, "no rest to pay");
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.ReceivedSats, Is.EqualTo(15_000.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)));
    }

    [Test]
    public async Task Past_its_end_the_page_waits_for_the_final_status()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(16));
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("ended"));
        Assert.That(page.QrSvg, Is.Null);
        Assert.That(page.CanMakeNew, Is.False, "not before a check found it expired");
        Assert.That(page.PollSeconds, Is.EqualTo(5));
    }

    [Test]
    public async Task Expired_unpaid_offers_a_new_invoice_underpaid_does_not()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await Reports(SdkData.Expired());
        var page = await PageAsync();
        Assert.That(page.Phase, Is.EqualTo("expired"));
        Assert.That(page.CanMakeNew, Is.True);
        Assert.That(page.HasReceived, Is.False);
        Assert.That(page.PollSeconds, Is.Null);

        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var record = _shop.Database.Orders().Single(stored => stored.OrderGuid == order.OrderGuid);
        var underpaid = await _shop.NewScope().Factory.PreparePaymentPageAsync(record, _shop.Order(order.Id), _shop.Database.Invoices().Single(stored => stored.Id == invoice.Id));
        Assert.That(underpaid.Phase, Is.EqualTo("expired"));
        Assert.That(underpaid.HasReceived, Is.True);
        Assert.That(underpaid.CanMakeNew, Is.False);
    }

    [Test]
    public async Task Paid_says_so_and_stops_asking()
    {
        await Reports(SdkData.PaidLightning());
        var page = await PageAsync();

        Assert.That(page.Phase, Is.EqualTo("paid"));
        Assert.That(page.PollSeconds, Is.Null);
        Assert.That(page.Uri, Is.Null);
    }

    [Test]
    public async Task The_version_changes_with_what_the_page_shows()
    {
        var open = (await PageAsync()).Version;
        await Reports(SdkData.Open(_invoice.ExpiresAt));
        Assert.That((await PageAsync()).Version, Is.EqualTo(open), "nothing new: no reload");
        await Reports(SdkData.Unconfirmed(24_500, 0));
        var unconfirmed = (await PageAsync()).Version;
        Assert.That(unconfirmed, Is.Not.EqualTo(open));
        await Reports(SdkData.Unconfirmed(24_500, 1));
        Assert.That((await PageAsync()).Version, Is.Not.EqualTo(unconfirmed));
    }

    [TestCase("javascript:alert(1)")]
    [TestCase("https://evil.example/")]
    [TestCase("data:text/html,<script>alert(1)</script>")]
    public async Task Never_links_to_anything_but_a_payment_URI(string uri)
    {
        _shop.Gateway.Create = (order, _) =>
        {
            var json = SdkData.InvoiceJson(order.Ref, order.Fiat!.Amount, order.Fiat.Currency, _shop.Time.UnixNow, _shop.Time.UnixNow + 900, order.Note!);
            json["paymentUri"] = uri;
            return Task.FromResult(Invoice.FromJson(json.ToJsonString()));
        };
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        var record = _shop.Database.Orders().Single(stored => stored.OrderGuid == order.OrderGuid);
        var page = await _shop.NewScope().Factory.PreparePaymentPageAsync(record, order, invoice);

        Assert.That(page.Uri, Is.Null);
        Assert.That(page.QrSvg, Is.Null);
    }

    [Test]
    public async Task The_order_s_page_links_to_the_payment_page_with_its_secret()
    {
        var model = await _shop.NewScope().Factory.PrepareOrderPaymentAsync(_record, _shop.Order(_order.Id), _invoice);

        Assert.That(model.Phase, Is.EqualTo("open"));
        Assert.That(model.PayUrl, Does.Contain(_record.PageToken).And.Contain(_order.OrderGuid.ToString()));
    }

    private Task<Models.OrderPaymentModel> OrderBlockAsync()
    {
        return _shop.NewScope().Factory.PrepareOrderPaymentAsync(_record, _shop.Order(_order.Id), _shop.Database.Invoices().Last());
    }

    [Test]
    public async Task The_order_s_page_says_what_the_payment_page_says_of_an_underpayment_that_ended()
    {
        // 5 000 of 24 500 sats came on-chain, then the invoice ended: the payment page says only
        // that much came, the merchant will contact the customer, and offers no new invoice.
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 5_000, 19_500));
        Assert.That((await OrderBlockAsync()).ShowPayLink, Is.True, "the rest is to pay on the page");
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await Reports(SdkData.Expired(5_000));

        var page = await PageAsync();
        var block = await OrderBlockAsync();
        Assert.That(page.Phase, Is.EqualTo("expired"));
        Assert.That(page.CanMakeNew, Is.False);
        Assert.That(block.Phase, Is.EqualTo("expired"));
        Assert.That(block.HasReceived, Is.True);
        Assert.That((block.ReceivedSats, block.Sats), Is.EqualTo((page.ReceivedSats, page.Sats)));
        Assert.That(block.ReceivedSats, Is.EqualTo(Sats(5_000)));
        Assert.That(block.Sats, Is.EqualTo(Sats(24_500)));
        Assert.That(block.ShowPayLink, Is.False, "the payment page has nothing more");
        var english = Texts.Of("en");
        Assert.That(english.Format("Order.Status.expiredUnderpaid", "5,000", "24,500"), Does.Contain("5,000 of 24,500 sats came in time").And.Contain("will contact you"));
    }

    [Test]
    public async Task The_order_s_page_leads_to_the_payment_page_while_it_shows_more()
    {
        Assert.That((await OrderBlockAsync()).ShowPayLink, Is.True, "the invoice to pay");
        await Reports(SdkData.Unconfirmed(24_500, 0));
        Assert.That((await OrderBlockAsync()).ShowPayLink, Is.True, "the confirmations");
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await Reports(SdkData.Expired());
        var block = await OrderBlockAsync();
        Assert.That(block.HasReceived, Is.False);
        Assert.That(block.ShowPayLink, Is.True, "a new invoice");

        var paid = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(paid)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var record = _shop.Database.Orders().Single(stored => stored.OrderGuid == paid.OrderGuid);
        var paidBlock = await _shop.NewScope().Factory.PrepareOrderPaymentAsync(record, _shop.Order(paid.Id), _shop.Database.Invoices().Last());
        Assert.That(paidBlock.Phase, Is.EqualTo("paid"));
        Assert.That(paidBlock.ShowPayLink, Is.False);
        Assert.That(paidBlock.HasReceived, Is.False);
    }

    [Test]
    public async Task The_admin_sees_the_reference_amounts_rate_address_transactions_proof_and_checks()
    {
        await Reports(SdkData.PaidMixed(24_500));
        var model = await _shop.NewScope().Factory.PrepareAdminOrderPaymentAsync(_shop.Order(_order.Id), _shop.Database.Invoices());

        var invoice = model.Invoices.Single();
        Assert.That(invoice.Reference, Is.EqualTo(_invoice.Reference));
        Assert.That(invoice.State, Is.EqualTo(_shop.Texts.Format("State.paid")));
        Assert.That(invoice.Amount, Does.Contain("BTC").And.Contain("24.50 CHF").And.Contain("sats"));
        Assert.That(invoice.Rate, Does.Contain("100000").And.Contain("coingecko"));
        Assert.That(invoice.Address, Is.EqualTo(SdkData.ADDRESS));
        Assert.That(invoice.AddressLink, Is.True);
        Assert.That(invoice.Txids, Is.EqualTo(new[] { SdkData.TXID }));
        Assert.That(invoice.Preimages, Is.EqualTo(new[] { SdkData.PREIMAGE }));
        Assert.That(invoice.Received, Is.EqualTo(_shop.Texts.Format("Admin.ReceivedMixed", Sats(24_500))));
        Assert.That(invoice.LastCheck, Is.Not.Null);
        Assert.That(invoice.NextCheck, Is.Null);
        Assert.That(invoice.Error, Is.Null);
        Assert.That(model.Warning, Is.Null);
    }

    private static string Sats(long sats)
    {
        return sats.ToString("N0", System.Globalization.CultureInfo.CurrentCulture);
    }

    private async Task<Models.AdminInvoiceModel> AdminInvoiceAsync()
    {
        return (await _shop.NewScope().Factory.PrepareAdminOrderPaymentAsync(_shop.Order(_order.Id), _shop.Database.Invoices())).Invoices.Single();
    }

    [Test]
    public async Task The_admin_sees_what_came_in_each_state_a_Lightning_payment_included()
    {
        Assert.That((await AdminInvoiceAsync()).Received, Is.Null, "not checked yet");
        await Reports(SdkData.Open(_invoice.ExpiresAt));
        Assert.That((await AdminInvoiceAsync()).Received, Is.EqualTo($"{Sats(0)} sats"));
        await Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500));
        Assert.That((await AdminInvoiceAsync()).Received, Is.EqualTo($"{Sats(20_000)} sats"));
        await Reports(SdkData.Unconfirmed(24_500, 1));
        Assert.That((await AdminInvoiceAsync()).Received, Is.EqualTo($"{Sats(24_500)} sats"));
        await Reports(SdkData.PaidOnchain(25_000));
        Assert.That((await AdminInvoiceAsync()).Received, Is.EqualTo(_shop.Texts.Format("Admin.ReceivedOnchain", Sats(25_000))));

        // Paid in Lightning: the SDK's status gives no received, the stored Received stays 0 (as
        // the SDK's JSON says), and the admin sees the Lightning invoice's amount.
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var stored = _shop.Database.Invoices().Single(row => row.Id == invoice.Id);
        Assert.That(stored.Received, Is.Zero);
        Assert.That(StoredInvoice.Of(stored)!.Came, Is.EqualTo(24_500));
        var admin = (await _shop.NewScope().Factory.PrepareAdminOrderPaymentAsync(_shop.Order(order.Id), [stored])).Invoices.Single();
        Assert.That(admin.Received, Is.EqualTo(_shop.Texts.Format("Admin.ReceivedLightning", Sats(24_500))));
        Assert.That(PaymentRules.SomethingCame(stored), Is.True);
    }

    [Test]
    public async Task The_admin_is_not_told_a_Lightning_payment_found_can_still_be_paid()
    {
        // Paid in Lightning, nopCommerce's completion failed: the invoice is not final yet, its
        // Received 0. The warning is the one of a payment that came, not "can still be paid".
        var scope = _shop.NewScope();
        scope.OrderProcessing.Setup(s => s.MarkOrderAsPaidAsync(It.IsAny<Order>())).ThrowsAsync(new InvalidOperationException("database down"));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await scope.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(_shop.Database.Invoices().Single().Final, Is.False);

        var model = await _shop.NewScope().Factory.PrepareAdminOrderPaymentAsync(_shop.Order(_order.Id), _shop.Database.Invoices());
        Assert.That(model.Warning, Is.EqualTo(_shop.Texts.Format("Admin.Warning.Confirming")));
    }

    [Test]
    public async Task The_admin_reads_errors_in_plain_words_and_is_warned_while_a_payment_may_come()
    {
        _shop.Gateway.Status = (_, _) => throw new NetworkException("down");
        await _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var model = await _shop.NewScope().Factory.PrepareAdminOrderPaymentAsync(_shop.Order(_order.Id), _shop.Database.Invoices());

        Assert.That(model.Invoices.Single().Error, Is.EqualTo(_shop.Texts.Format("ErrorCode.network")));
        Assert.That(model.Warning, Does.StartWith(_shop.Texts.Format("Admin.Warning.Payable", "")[..30]));

        Assert.That(await _shop.NewScope().Factory.ErrorTextAsync("something_new"), Is.EqualTo("something_new"));
    }

    [Test]
    public void Writes_bitcoins_exactly()
    {
        Assert.That(PaymentModelFactory.Btc(49_000), Is.EqualTo("0.00049"));
        Assert.That(PaymentModelFactory.Btc(100_000_000), Is.EqualTo("1"));
        Assert.That(PaymentModelFactory.Btc(1), Is.EqualTo("0.00000001"));
    }
}