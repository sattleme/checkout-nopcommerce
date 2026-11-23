using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moq;
using Nop.Core.Domain.Directory;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Payments;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>Making an order's invoice: the reference, the lock, one payable invoice per order, the SDK's answers.</summary>
[TestFixture]
public sealed partial class InvoiceMakerTests
{
    private Shop _shop = null!;

    [SetUp]
    public void SetUp()
    {
        _shop = new Shop();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private static ProcessPaymentRequest Placement(Guid guid, decimal total = 24.50m)
    {
        return new ProcessPaymentRequest { OrderGuid = guid, OrderTotal = total, StoreId = 1, CustomerId = 7 };
    }

    [GeneratedRegex("^nop-[0-9a-f]{32}$")]
    private static partial Regex ReferenceFormat();

    [Test]
    public async Task Makes_the_invoice_of_the_order_being_placed()
    {
        var guid = Guid.NewGuid();
        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.False);
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        var asked = _shop.Gateway.Orders.Single();
        Assert.That(asked.Ref, Does.Match(ReferenceFormat()));
        Assert.That(asked.Fiat, Is.EqualTo(new Price("24.50", "CHF")));
        Assert.That(asked.DurationSeconds, Is.EqualTo(15 * 60));
        Assert.That(asked.Note, Is.EqualTo("Sunrise Bakery · " + guid.ToString("N")[..8]));
        Assert.That(asked.Sats, Is.Null);
        Assert.That(asked.Checkout, Is.False, "never the merchant's checkout link: the shop's own page");

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Reference, Is.EqualTo(asked.Ref));
        Assert.That(stored.OrderGuid, Is.EqualTo(guid));
        Assert.That(stored.InvoiceJson, Is.Not.Null);
        Assert.That(Invoice.FromJson(stored.InvoiceJson!).Ref, Is.EqualTo(asked.Ref), "the SDK's own JSON");
        Assert.That(stored.ExpiresAt, Is.EqualTo(_shop.Time.UnixNow + 900));
        Assert.That(stored.NextCheckOnUtc, Is.Not.Null);
        Assert.That(stored.Error, Is.Empty);
        var record = _shop.Database.Orders().Single();
        Assert.That(record.PageToken, Does.Match("^[0-9a-f]{64}$"));
        Assert.That(record.LockId, Is.Null, "the lock is released");
    }

    [Test]
    public async Task Stores_the_reference_before_the_SDK_is_asked()
    {
        IList<Domain.SattlemeInvoice>? before = null;
        _shop.Gateway.Create = (order, _) =>
        {
            before = _shop.Database.Invoices();
            return Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        };
        await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(before, Has.Count.EqualTo(1));
        Assert.That(before![0].Reference, Is.EqualTo(_shop.Gateway.Orders.Single().Ref));
        Assert.That(before[0].InvoiceJson, Is.Null);
        var locked = _shop.Database.Orders().Single();
        Assert.That(locked.LockId, Is.Null);
    }

    [Test]
    public async Task Holds_the_order_locked_in_the_database_while_the_SDK_makes_the_invoice()
    {
        Domain.SattlemeOrder? during = null;
        _shop.Gateway.Create = (order, _) =>
        {
            during = _shop.Database.Orders().Single();
            return Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        };
        await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(during!.LockId, Does.Match("^[0-9a-f]{32}$"));
        Assert.That(during.LockedUntilUtc, Is.GreaterThan(_shop.Time.UtcNow));
        Assert.That(_shop.Database.Orders().Single().LockId, Is.Null);
    }

    [Test]
    public async Task Makes_a_new_random_reference_for_each_order()
    {
        for (var i = 0; i < 5; i++)
            await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        var references = _shop.Database.Invoices().Select(invoice => invoice.Reference).ToList();
        Assert.That(references, Is.Unique);
        Assert.That(references, Has.All.Match(ReferenceFormat()));
    }

    [Test]
    public async Task Two_requests_at_once_get_one_invoice()
    {
        var release = new TaskCompletionSource();
        _shop.Gateway.Create = async (order, _) =>
        {
            await release.Task;
            return SdkData.InvoiceFor(order, _shop.Time.UnixNow);
        };
        var guid = Guid.NewGuid();
        var first = _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));
        var second = _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));
        // Both are under way: one holds the lock and waits for the SDK, the other waits for the lock.
        await WaitUntil(() => _shop.Gateway.Creates == 1);
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        Assert.That(results.Select(result => result.Invoice?.Id), Is.All.Not.Null);
        Assert.That(results[0].Invoice!.Id, Is.EqualTo(results[1].Invoice!.Id));
        Assert.That(_shop.Database.Invoices(), Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Hands_out_the_order_s_payable_invoice_again()
    {
        var guid = Guid.NewGuid();
        var first = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));
        var again = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));

        Assert.That(again.Invoice!.Id, Is.EqualTo(first.Invoice!.Id));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
    }

    [Test]
    public async Task Makes_no_second_invoice_while_one_may_be_paid()
    {
        var guid = Guid.NewGuid();
        var first = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));
        var other = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid, 30m));

        Assert.That(other.Invoice, Is.Null);
        Assert.That(other.Refusal, Is.EqualTo("Error.Pending"));
        Assert.That(other.RefusalArguments.Single(), Is.EqualTo(DateTimeOffset.FromUnixTimeSeconds(first.Invoice!.ExpiresAt + SattlemeDefaults.GRACE_SECONDS).UtcDateTime));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));

        // Its grace period over, its status unknown yet: still one that may be paid if something came.
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Database.Change(first.Invoice.Id, invoice =>
        {
            invoice.State = "unconfirmed";
            invoice.Received = 24_500;
        });
        var confirming = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid, 30m));
        Assert.That(confirming.Refusal, Is.EqualTo("Error.Confirming"));
    }

    [Test]
    public async Task Makes_a_new_invoice_once_the_last_one_ended_unpaid()
    {
        var order = _shop.AddOrder();
        var first = await _shop.NewScope().Maker.MakeAgainAsync(order);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(first.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5)), Is.True);

        var second = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));

        Assert.That(second.Invoice, Is.Not.Null);
        Assert.That(second.Invoice!.Id, Is.Not.EqualTo(first.Invoice.Id));
        Assert.That(second.Invoice.Reference, Is.Not.EqualTo(first.Invoice.Reference));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(2));
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.Created", second.Invoice.Reference, "24,500", End(second.Invoice))));
    }

    /// <summary>The end of <paramref name="invoice"/>, as its note writes it.</summary>
    private static string End(Domain.SattlemeInvoice invoice)
    {
        return DateTimeOffset.FromUnixTimeSeconds(invoice.ExpiresAt).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Test]
    public async Task Notes_the_invoice_made_at_placement_once_the_order_is_placed()
    {
        // The invoice is made before nopCommerce stores the order: no order to note it on yet.
        var order = _shop.AddOrder();
        var made = (await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(order.OrderGuid))).Invoice!;
        Assert.That(_shop.Notes, Is.Empty);

        await new OrderPlacedConsumer(_shop.NewScope().Notes, _shop.NewScope().Store).HandleEventAsync(new Core.Domain.Orders.OrderPlacedEvent(order));

        var note = _shop.Notes.Single();
        Assert.That(note.OrderId, Is.EqualTo(order.Id));
        Assert.That(note.DisplayToCustomer, Is.False, "the admin's only");
        Assert.That(note.Note, Is.EqualTo(_shop.Texts.Format("Note.Created", made.Reference, "24,500", End(made))));
        Assert.That(End(made), Is.EqualTo(_shop.Time.UtcNow.AddMinutes(15).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)));
        Assert.That(note.Note, Does.Not.Contain(SdkData.ADDRESS).And.Not.Contain("lnbc"), "never an address nor the invoice itself");
    }

    [Test]
    public async Task Notes_how_an_invoice_can_be_paid()
    {
        // Below the on-chain minimum: a Lightning invoice only.
        _shop.Gateway.Create = (order, _) => Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow, onchain: false));
        var lightning = _shop.AddOrder();
        var made = (await _shop.NewScope().Maker.MakeAgainAsync(lightning)).Invoice!;
        Assert.That(_shop.NoteTexts, Is.EqualTo(new[] { _shop.Texts.Format("Note.CreatedLightning", made.Reference, "24,500", End(made)) }));

        // Above the Lightning address's maximum: on-chain only.
        _shop.Gateway.Create = (order, _) =>
        {
            var json = SdkData.InvoiceJson(order.Ref!, order.Fiat!.Amount, order.Fiat.Currency, _shop.Time.UnixNow, _shop.Time.UnixNow + 900, order.Note!);
            json["bolt11"] = null;
            json["paymentHash"] = null;
            json["verifyUrl"] = null;
            json["paymentUri"] = $"bitcoin:{SdkData.ADDRESS}?amount=0.000245";
            return Task.FromResult(Invoice.FromJson(json.ToJsonString()));
        };
        var onchain = _shop.AddOrder();
        made = (await _shop.NewScope().Maker.MakeAgainAsync(onchain)).Invoice!;
        Assert.That(_shop.NoteTexts[^1], Is.EqualTo(_shop.Texts.Format("Note.CreatedOnchain", made.Reference, "24,500", End(made))));
    }

    [Test]
    public async Task Notes_nothing_on_an_order_of_another_method()
    {
        var order = _shop.AddOrder();
        await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(order.OrderGuid));
        _shop.Edit(order.Id, stored => stored.PaymentMethodSystemName = "Payments.CheckMoneyOrder");

        await new OrderPlacedConsumer(_shop.NewScope().Notes, _shop.NewScope().Store).HandleEventAsync(new Core.Domain.Orders.OrderPlacedEvent(_shop.Order(order.Id)));

        Assert.That(_shop.Notes, Is.Empty);
    }

    [Test]
    public async Task Makes_no_new_invoice_when_the_time_is_over_until_a_check_finds_it_expired()
    {
        var order = _shop.AddOrder();
        var first = await _shop.NewScope().Maker.MakeAgainAsync(order);
        _shop.Time.Advance(TimeSpan.FromMinutes(20));

        var refused = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));
        Assert.That(refused.Invoice!.Id, Is.EqualTo(first.Invoice!.Id), "the same price: that invoice, still being checked");
        _shop.Edit(order.Id, stored => stored.OrderTotal = 30m);
        var other = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));
        Assert.That(other.Refusal, Is.EqualTo("Error.Checking"));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(first.Invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var again = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));
        Assert.That(again.Invoice, Is.Not.Null);
        Assert.That(_shop.Gateway.Orders.Last().Fiat!.Amount, Is.EqualTo("30.00"));
    }

    [Test]
    public async Task Makes_no_new_invoice_after_an_underpayment()
    {
        var order = _shop.AddOrder();
        var first = await _shop.NewScope().Maker.MakeAgainAsync(order);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await _shop.NewScope().Checker.CheckAsync(first.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var second = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));

        Assert.That(second.Refusal, Is.EqualTo("Error.Underpaid"));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
    }

    [Test]
    public async Task Asks_for_the_same_reference_again_after_a_failed_try()
    {
        var calls = 0;
        _shop.Gateway.Create = (order, _) => ++calls == 1
            ? throw new NetworkException("relays unreachable")
            : Task.FromResult(SdkData.InvoiceFor(order, _shop.Time.UnixNow));
        var guid = Guid.NewGuid();
        var failed = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));
        Assert.That(failed.Refusal, Is.EqualTo("Error.TryAgain"));
        Assert.That(failed.ErrorCode, Is.EqualTo("network"));
        Assert.That(_shop.Database.Invoices().Single().Error, Is.EqualTo("network"));

        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(guid));

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(_shop.Gateway.Orders.Select(order => order.Ref).Distinct().Count(), Is.EqualTo(1));
        Assert.That(_shop.Database.Invoices(), Has.Count.EqualTo(1));
        Assert.That(_shop.Database.Invoices().Single().Error, Is.Empty);
    }

    [Test]
    public async Task Takes_the_invoice_the_SDK_made_for_an_earlier_try_InvoicePending()
    {
        Invoice? earlier = null;
        _shop.Gateway.Create = (order, _) =>
        {
            earlier = SdkData.InvoiceFor(order, _shop.Time.UnixNow - 60);
            throw new InvoicePendingException(SdkData.Declared(earlier), null);
        };
        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.False);
        var json = JsonNode.Parse(made.Invoice!.InvoiceJson!)!;
        Assert.That((string?)json["ref"], Is.EqualTo(earlier!.Ref));
        Assert.That(json["declaredAt"], Is.Not.Null, "the declared invoice, as the SDK gave it");
        Assert.That(made.Invoice.ExpiresAt, Is.EqualTo(earlier.ExpiresAt));
    }

    [Test]
    public async Task Stores_an_invoice_paid_already_and_completes_it_with_the_next_check_OrderPaid()
    {
        var order = _shop.AddOrder();
        _shop.Gateway.Create = (asked, _) =>
            throw new OrderPaidException(SdkData.Declared(SdkData.InvoiceFor(asked, _shop.Time.UnixNow)), (PaymentStatus.Paid)SdkData.PaidLightning());
        var made = await _shop.NewScope().Maker.MakeAgainAsync(order);
        Assert.That(made.Invoice, Is.Not.Null);
        Assert.That(made.Paid, Is.True);

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(made.Invoice!.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(Core.Domain.Payments.PaymentStatus.Paid));
        Assert.That(_shop.Order(order.Id).CaptureTransactionId, Is.EqualTo(made.Invoice.Reference));
    }

    private static IEnumerable<TestCaseData> SdkErrors()
    {
        yield return new TestCaseData(new NetworkException("down"), "network", "Error.TryAgain", "warning");
        yield return new TestCaseData(new RateException("no rate"), "rate", "Error.TryAgain", "warning");
        yield return new TestCaseData(new OnchainUnavailableException(), "onchain_unavailable", "Error.TryAgain", "warning");
        yield return new TestCaseData(new ChannelNotFoundException(false), "channel_not_found", "Error.TryAgain", "warning");
        yield return new TestCaseData(new OperationCanceledException(), "timeout", "Error.TryAgain", "warning");
        yield return new TestCaseData(new InvalidOrderException("fiat", "above the merchant's maximum"), "invalid_order", "Error.NotPayable", "warning");
        yield return new TestCaseData(new UnsupportedCurrencyException("CHF", "coingecko"), "unsupported_currency", "Error.NotPayable", "warning");
        yield return new TestCaseData(new RevokedException(), "revoked", "Error.Unavailable", "error");
        yield return new TestCaseData(new ChannelNotFoundException(true), "channel_not_found", "Error.Unavailable", "error");
        yield return new TestCaseData(new InvalidOptionException("relayTimeoutMs", "out of range"), "invalid_option", "Error.Unavailable", "error");
        yield return new TestCaseData(new CheckoutUnavailableException(), "checkout_unavailable", "Error.Unavailable", "error");
        yield return new TestCaseData(new InvalidOperationException("a bug"), "error", "Error.Unavailable", "error");
    }

    [TestCaseSource(nameof(SdkErrors))]
    public async Task Refuses_the_order_with_a_message_safe_to_show_when_the_SDK_fails(Exception error, string code, string refusal, string level)
    {
        _shop.Gateway.Create = (_, _) => throw error;
        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(made.Invoice, Is.Null);
        Assert.That(made.ErrorCode, Is.EqualTo(code));
        Assert.That(made.Refusal, Is.EqualTo(refusal));
        Assert.That(_shop.Texts.Get(SattlemeDefaults.RESOURCE_PREFIX + refusal), Is.Not.Null.And.Not.Contains(error.Message));
        Assert.That(_shop.Logged(level), Has.Some.Contains(code));
        Assert.That(_shop.Log.Select(entry => entry.Message), Has.None.Contains(SdkData.KEY), "never the key");
        Assert.That(_shop.Database.Invoices().Single().Error, Is.EqualTo(code));
    }

    [Test]
    public async Task Never_shows_an_invoice_of_another_price()
    {
        _shop.Gateway.Create = (order, _) => Task.FromResult(Invoice.FromJson(SdkData.InvoiceJson(order.Ref, "2.45", "CHF", _shop.Time.UnixNow, _shop.Time.UnixNow + 900, order.Ref).ToJsonString()));
        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(made.Invoice, Is.Null);
        Assert.That(made.ErrorCode, Is.EqualTo("mismatch"));
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Is.Null);
    }

    [Test]
    public async Task Refuses_without_a_key_that_can_be_read()
    {
        _shop.Settings[0].EncryptedIntegrationKey = Encryption.Encrypt(SdkData.KEY, "another store's encryption key");
        var made = await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(made.Refusal, Is.EqualTo("Error.Unavailable"));
        Assert.That(made.ErrorCode, Is.EqualTo("no_key"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [TestCase(24.50, "CHF", "24.50")]
    [TestCase(24.501, "CHF", "24.51")]
    [TestCase(0.001, "EUR", "0.01")]
    [TestCase(1500.2, "JPY", "1501")]
    [TestCase(10.0005, "BHD", "10.001")]
    public async Task Asks_for_the_total_in_the_primary_currency_rounded_up(decimal total, string currency, string amount)
    {
        _shop.PrimaryCurrency = new Currency { Id = 1, CurrencyCode = currency };
        var price = await _shop.NewScope().Maker.PriceOfAsync(total);

        Assert.That(price, Is.EqualTo(new PriceToPay(amount, currency)));
    }

    [TestCase("XAU")]
    [TestCase("ABC")]
    public async Task Offers_no_price_in_a_currency_the_SDK_does_not_convert(string currency)
    {
        _shop.PrimaryCurrency = new Currency { Id = 1, CurrencyCode = currency };
        var scope = _shop.NewScope();

        Assert.That(await scope.Maker.PriceOfAsync(10m), Is.Null);
        var made = await scope.Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));
        Assert.That(made.Refusal, Is.EqualTo("Error.NotPayable"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [Test]
    public async Task Offers_no_price_for_a_total_of_zero()
    {
        Assert.That(await _shop.NewScope().Maker.PriceOfAsync(0m), Is.Null);
    }

    [Test]
    public async Task Refuses_a_new_invoice_for_an_order_not_pending_payment()
    {
        var order = _shop.AddOrder();
        _shop.Edit(order.Id, stored => stored.OrderStatus = Core.Domain.Orders.OrderStatus.Cancelled);

        var made = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));

        Assert.That(made.Refusal, Is.EqualTo("Error.NotPending"));
        Assert.That(_shop.Gateway.Creates, Is.Zero);
    }

    [Test]
    public async Task Writes_the_time_to_pay_the_admin_set()
    {
        _shop.Settings[0].MinutesToPay = 42;
        await _shop.NewScope().Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        Assert.That(_shop.Gateway.Orders.Single().DurationSeconds, Is.EqualTo(42 * 60));
    }

    [Test]
    public async Task Keeps_the_note_within_the_SDK_s_100_characters()
    {
        var scope = _shop.NewScope();
        scope.StoreService.Setup(s => s.GetStoreByIdAsync(1)).ReturnsAsync(new Core.Domain.Stores.Store { Name = new string('é', 150) + "\u0007\n" });
        await scope.Maker.MakeForPlacementAsync(Placement(Guid.NewGuid()));

        var note = _shop.Gateway.Orders.Single().Note!;
        Assert.That(note.EnumerateRunes().Count(), Is.LessThanOrEqualTo(100));
        Assert.That(note.EnumerateRunes().Any(System.Text.Rune.IsControl), Is.False);
        Assert.That(note, Does.EndWith(" · " + _shop.Database.Orders().Single().OrderGuid.ToString("N")[..8]));
    }

    internal static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("The condition never held");
            await Task.Delay(20);
        }
    }
}