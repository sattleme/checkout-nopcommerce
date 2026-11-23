using Moq;
using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;
using Order = Nop.Core.Domain.Orders.Order;
using SdkPaymentStatus = NopSattleme.Sattleme.Checkout.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>What the checks find, and what becomes of the order: every state the SDK reports, and its errors.</summary>
[TestFixture]
public sealed class PaymentCheckerTests
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(5);
    private Shop _shop = null!;
    private Order _order = null!;
    private SattlemeInvoice _invoice = null!;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        _order = _shop.AddOrder();
        _invoice = (await _shop.NewScope().Maker.MakeAgainAsync(_order)).Invoice!;
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private void Reports(SdkPaymentStatus status)
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(status);
    }

    private Task<bool> CheckAsync()
    {
        return _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);
    }

    private SattlemeInvoice Stored => _shop.Database.Invoices().Single(invoice => invoice.Id == _invoice.Id);

    private static string Sats(long sats)
    {
        return sats.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
    }

    [Test]
    public async Task Checks_the_invoice_the_order_stored()
    {
        IInvoiceToCheck? checkedInvoice = null;
        _shop.Gateway.Status = (invoice, _) =>
        {
            checkedInvoice = invoice;
            return Task.FromResult(SdkData.Open(_invoice.ExpiresAt));
        };
        Assert.That(await CheckAsync(), Is.True);

        Assert.That(checkedInvoice!.Ref, Is.EqualTo(_invoice.Reference));
        Assert.That(_shop.Gateway.Keys, Has.All.EqualTo(SdkData.KEY));
    }

    [Test]
    public async Task Open_waits_for_the_payment()
    {
        Reports(SdkData.Open(_invoice.ExpiresAt));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("open"));
        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.CheckedOnUtc, Is.EqualTo(_shop.Time.UtcNow).Within(TimeSpan.FromSeconds(1)));
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));
        Assert.That(_shop.MarkedPaid, Is.Zero);
    }

    [Test]
    public async Task Open_with_a_rest_keeps_the_rest_to_show()
    {
        Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 20_000, 4_500));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("open"));
        Assert.That(Stored.Received, Is.EqualTo(20_000));
        var shown = StoredInvoice.Of(Stored)!;
        Assert.That(shown.RestSats, Is.EqualTo(4_500));
        Assert.That(shown.RestPaymentUri, Does.StartWith("lightning:"));
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Underpaid));
        Assert.That(_shop.MarkedPaid, Is.Zero);
    }

    [Test]
    public async Task Unconfirmed_waits_with_one_note()
    {
        Reports(SdkData.Unconfirmed(24_500, 1));
        await CheckAsync();
        Reports(SdkData.Unconfirmed(24_500, 2));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("unconfirmed"));
        Assert.That(Stored.Final, Is.False);
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.NoteTexts.Count(note => note == _shop.Texts.Format("Note.Unconfirmed", _invoice.Reference, Sats(24_500), Sats(24_500), 1)), Is.EqualTo(1));
        Assert.That(_shop.NoteTexts, Has.None.Contains(SdkData.TXID), "no transaction in a note");
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Unconfirmed));
    }

    private static IEnumerable<TestCaseData> Paid()
    {
        yield return new TestCaseData(SdkData.PaidLightning(), "paid: lightning");
        yield return new TestCaseData(SdkData.PaidOnchain(24_500), "paid: on-chain");
        yield return new TestCaseData(SdkData.PaidMixed(24_500), "paid: on-chain and lightning");
    }

    [TestCaseSource(nameof(Paid))]
    public async Task Paid_marks_the_order_paid_through_nopCommerce_with_the_reference(SdkPaymentStatus status, string result)
    {
        Reports(status);
        Assert.That(await CheckAsync(), Is.True);

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(NopPaymentStatus.Paid));
        Assert.That(order.CaptureTransactionId, Is.EqualTo(_invoice.Reference));
        Assert.That(order.CaptureTransactionResult, Is.EqualTo(result));
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(Stored.Final, Is.True);
        Assert.That(Stored.NextCheckOnUtc, Is.Null);
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Paid));
    }

    [Test]
    public async Task Completes_the_order_once_whatever_checks_it_again()
    {
        Reports(SdkData.PaidLightning());
        await CheckAsync();
        Assert.That(await CheckAsync(), Is.False, "a final invoice is never checked again");
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, SattlemeDefaults.PageInterval, _timeout), Is.False);
        await _shop.NewScope().Task.ExecuteAsync();

        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));
    }

    [Test]
    public async Task Completes_the_order_once_when_the_page_and_the_background_check_at_once()
    {
        var release = new TaskCompletionSource();
        _shop.Gateway.Status = async (_, _) =>
        {
            await release.Task;
            return SdkData.PaidLightning();
        };
        var checks = Enumerable.Range(0, 4).Select(_ => _shop.NewScope().Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout)).ToList();
        await InvoiceMakerTests.WaitUntil(() => _shop.Gateway.Statuses >= 1);
        await Task.Delay(300);
        release.SetResult();
        var ran = await Task.WhenAll(checks);

        Assert.That(ran.Count(done => done), Is.EqualTo(1), "one check holds the invoice at a time");
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
    }

    [Test]
    public async Task A_check_that_outlived_its_lease_decides_nothing()
    {
        var first = _shop.NewScope();
        var paused = new TaskCompletionSource();
        var resume = new TaskCompletionSource();
        _shop.Gateway.Status = async (_, _) =>
        {
            paused.TrySetResult();
            await resume.Task;
            return SdkData.Expired();
        };
        var late = first.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout);
        await paused.Task;
        // Its lease ends; another check takes the invoice, and finds it paid.
        _shop.Time.Advance(_timeout + SattlemeDefaults.LeaseMargin + TimeSpan.FromSeconds(1));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        Assert.That(await CheckAsync(), Is.True);
        resume.SetResult();
        await late;

        Assert.That(Stored.State, Is.EqualTo("paid"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Paid));
        Assert.That(_shop.NoteTexts, Has.None.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));
    }

    [Test]
    public async Task Expired_ends_the_invoice_with_a_note()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Reports(SdkData.Expired());
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("expired"));
        Assert.That(Stored.Final, Is.True);
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Expired));
        Assert.That(PaymentRules.MayBePaid(Stored), Is.False);
    }

    [Test]
    public async Task Expired_with_something_received_notes_what_reached_the_wallet()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        Reports(SdkData.Expired(5_000));
        await CheckAsync();

        Assert.That(Stored.Final, Is.True);
        Assert.That(Stored.Received, Is.EqualTo(5_000));
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.Underpaid", _invoice.Reference, Sats(5_000), Sats(24_500))));
        Assert.That(_shop.Logged("warning"), Has.Some.Contains("underpaid"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));
    }

    [Test]
    public async Task Expired_reported_before_the_end_of_the_grace_period_is_not_final()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(60));
        Reports(SdkData.Expired());
        await CheckAsync();

        Assert.That(Stored.Final, Is.False, "a payment of its last seconds may still be found");
        Assert.That(PaymentRules.MayBePaid(Stored), Is.True);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Ended));
        Assert.That(_shop.NoteTexts, Has.None.EqualTo(_shop.Texts.Format("Note.Expired", _invoice.Reference)));

        _shop.Time.Advance(TimeSpan.FromSeconds(61));
        await CheckAsync();
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Expired));
    }

    [Test]
    public async Task Compares_the_invoice_with_what_the_order_still_owes()
    {
        // 30.00 CHF, 5.50 of it refunded: the order owes 24.50, the invoice's price.
        var order = _shop.AddOrder(30m);
        _shop.Edit(order.Id, stored => stored.RefundedAmount = 5.50m);
        var made = await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id));
        Assert.That(_shop.Gateway.Orders.Last().Fiat!.Amount, Is.EqualTo("24.50"));
        Reports(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(made.Invoice!.Id, TimeSpan.Zero, _timeout);

        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Paid));
    }

    /// <summary>The notes of the invoice after the one of its making.</summary>
    private IList<string> NotesAfterMaking => [.. _shop.NoteTexts.Where(note => note.Contains(_invoice.Reference, StringComparison.Ordinal)).Skip(1)];

    private static IEnumerable<TestCaseData> PaidNotes()
    {
        // The invoice asks 24 500 sats. Each note says what came, how, against that amount.
        yield return new TestCaseData(SdkData.PaidLightning(), new[] { ("Note.PaidLightning", new object[] { Sats(24_500) }) })
            .SetName("Paid_notes_say_what_came_in_Lightning");
        yield return new TestCaseData(SdkData.PaidOnchain(24_500), new[] { ("Note.PaidOnchain", new object[] { Sats(24_500), Sats(24_500), 1 }) })
            .SetName("Paid_notes_say_what_came_on_chain_exactly");
        yield return new TestCaseData(SdkData.PaidOnchain(25_000), new[]
        {
            ("Note.PaidOnchain", new object[] { Sats(25_000), Sats(24_500), 1 }),
            ("Note.PaidOver", new object[] { Sats(500) }),
        }).SetName("Paid_notes_say_what_came_on_chain_overpaid");
        yield return new TestCaseData(SdkPaymentStatus.Paid.Onchain("24500", [SdkData.TXID, SdkData.TXID2]), new[]
        {
            ("Note.PaidOnchain", new object[] { Sats(24_500), Sats(24_500), 2 }),
        }).SetName("Paid_notes_say_what_came_on_chain_in_two_transactions");
        yield return new TestCaseData(SdkPaymentStatus.Paid.Mixed("24600", [SdkData.TXID], [SdkData.PREIMAGE]), new[]
        {
            ("Note.PaidMixed", new object[] { Sats(24_600), Sats(24_500), 1, 1 }),
            ("Note.PaidOver", new object[] { Sats(100) }),
        }).SetName("Paid_notes_say_what_came_on_chain_and_in_Lightning");
        yield return new TestCaseData(SdkData.PaidOnchain(23_500), new[]
        {
            ("Note.PaidOnchain", new object[] { Sats(23_500), Sats(24_500), 1 }),
            ("Note.PaidShort", new object[] { Sats(1_000) }),
        }).SetName("Paid_notes_say_what_came_within_the_merchant_s_tolerance");
    }

    [TestCaseSource(nameof(PaidNotes))]
    public async Task Paid_notes_state_what_came_how_and_against_what(SdkPaymentStatus status, (string Resource, object[] Arguments)[] expected)
    {
        Reports(status);
        await CheckAsync();

        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Paid));
        Assert.That(NotesAfterMaking, Is.EqualTo(expected.Select(note => _shop.Texts.Format(note.Resource, [_invoice.Reference, .. note.Arguments]))));
        Assert.That(_shop.NoteTexts, Has.None.Contains(SdkData.TXID).And.None.Contains(SdkData.PREIMAGE), "no transaction, no proof in a note");
    }

    [Test]
    public async Task A_mixed_payment_being_confirmed_is_noted_without_a_split_the_status_does_not_give()
    {
        // 16 000 of 24 500 sats came on-chain; the rest, 8 500, was paid in Lightning; the on-chain
        // part waits for its confirmations. The status gives what came in all (24 500), never how
        // much of it came in Lightning: the note never says it all came on-chain.
        Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 16_000, 8_500));
        await CheckAsync();
        Reports(SdkData.Unconfirmed(24_500, 0));
        await CheckAsync();
        Reports(SdkPaymentStatus.Paid.Mixed("24500", [SdkData.TXID], [SdkData.PREIMAGE]));
        await CheckAsync();

        var english = Texts.Of("en");
        Assert.That(english.Get(SattlemeDefaults.RESOURCE_PREFIX + "Note.Unconfirmed"), Does.Contain("Lightning"), "says Lightning may be part of it");
        Assert.That(NotesAfterMaking, Is.EqualTo(new[]
        {
            _shop.Texts.Format("Note.Unconfirmed", _invoice.Reference, Sats(24_500), Sats(24_500), 0),
            _shop.Texts.Format("Note.PaidMixed", _invoice.Reference, Sats(24_500), Sats(24_500), 1, 1),
        }));
    }

    [Test]
    public async Task Completes_an_order_the_merchant_accepted_at_what_it_received_as_the_tolerance_does()
    {
        // 15 000 of 24 500 sats came on-chain: more short than any tolerance. The merchant accepted
        // the order at that in its app (SPEC.md §31): being confirmed, then paid, received below sats.
        Reports(SdkData.OpenWithRest(_invoice.ExpiresAt, 15_000, 9_500));
        await CheckAsync();
        Reports(SdkData.Unconfirmed(15_000, 1));
        await CheckAsync();

        Assert.That(Stored.State, Is.EqualTo("unconfirmed"));
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Unconfirmed));

        Reports(SdkData.PaidOnchain(15_000));
        Assert.That(await CheckAsync(), Is.True);

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(NopPaymentStatus.Paid), "never left pending for the amount alone");
        Assert.That(order.CaptureTransactionId, Is.EqualTo(_invoice.Reference));
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        // Its notes: made, being confirmed, paid, and how much came (the tolerance's note); no
        // other total, no underpayment.
        Assert.That(NotesAfterMaking, Is.EqualTo(new[]
        {
            _shop.Texts.Format("Note.Unconfirmed", _invoice.Reference, Sats(15_000), Sats(24_500), 1),
            _shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(15_000), Sats(24_500), 1),
            _shop.Texts.Format("Note.PaidShort", _invoice.Reference, Sats(9_500)),
        }));
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Paid));
    }

    [Test]
    public async Task Notes_nothing_more_when_the_exact_amount_came()
    {
        Reports(SdkData.PaidOnchain(24_500));
        await CheckAsync();

        Assert.That(NotesAfterMaking, Is.EqualTo(new[] { _shop.Texts.Format("Note.PaidOnchain", _invoice.Reference, Sats(24_500), Sats(24_500), 1) }), "made, then paid");
    }

    [Test]
    public async Task A_total_changed_since_the_invoice_keeps_the_order_pending_with_a_note()
    {
        // The admin edited the order's totals (Admin → Orders → Edit), then the old amount came.
        _shop.Edit(_order.Id, order => order.OrderTotal = 30m);
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        var order = _shop.Order(_order.Id);
        Assert.That(order.PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));
        Assert.That(order.OrderStatus, Is.EqualTo(OrderStatus.Pending));
        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOtherTotal", _invoice.Reference, Sats(24_500), "24.50 CHF", "30.00 CHF")));
        Assert.That(Stored.Final, Is.True, "never completed later on its own");
        Assert.That(PaymentRules.PhaseOf(order, Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Review));
    }

    [Test]
    public async Task A_primary_currency_changed_since_the_invoice_keeps_the_order_pending()
    {
        _shop.PrimaryCurrency = new Core.Domain.Directory.Currency { Id = 1, CurrencyCode = "EUR" };
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidOtherTotal", _invoice.Reference, Sats(24_500), "24.50 CHF", "24.50 EUR")));
    }

    [Test]
    public async Task A_payment_of_a_cancelled_order_is_noted_never_refused()
    {
        _shop.Edit(_order.Id, order => order.OrderStatus = OrderStatus.Cancelled);
        Reports(SdkData.PaidOnchain(24_500));
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidCancelled", _invoice.Reference, Sats(24_500))));
        Assert.That(Stored.Final, Is.True);
        Assert.That(PaymentRules.PhaseOf(_shop.Order(_order.Id), Stored, _shop.Time.UnixNow), Is.EqualTo(PaymentPhase.Review));
    }

    [Test]
    public async Task A_payment_of_an_order_paid_otherwise_is_noted()
    {
        _shop.Edit(_order.Id, order =>
        {
            order.PaymentStatus = NopPaymentStatus.Paid;
            order.CaptureTransactionId = "another-gateway-123";
        });
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.PaidTwice", _invoice.Reference, Sats(24_500))));
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task A_completion_that_failed_in_nopCommerce_is_done_again_later()
    {
        var scope = _shop.NewScope();
        scope.OrderProcessing.Setup(s => s.MarkOrderAsPaidAsync(It.IsAny<Order>())).ThrowsAsync(new InvalidOperationException("database down"));
        Reports(SdkData.PaidLightning());
        Assert.That(await scope.Checker.CheckAsync(_invoice.Id, TimeSpan.Zero, _timeout), Is.True);

        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.Error, Is.EqualTo("apply"));
        Assert.That(_shop.Logged("error"), Has.Some.Contains("apply"));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(NopPaymentStatus.Pending));

        // The completion it claimed may still be under way for a while; then the next check does it.
        await CheckAsync();
        Assert.That(_shop.MarkedPaid, Is.Zero);
        _shop.Time.Advance(SattlemeDefaults.CompletionStale + TimeSpan.FromSeconds(1));
        await CheckAsync();
        Assert.That(_shop.MarkedPaid, Is.EqualTo(1));
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task A_completion_cut_short_after_the_order_was_marked_paid_is_not_done_twice()
    {
        // nopCommerce marked the order paid, then the process stopped before the invoice was final.
        _shop.Edit(_order.Id, order =>
        {
            order.PaymentStatus = NopPaymentStatus.Paid;
            order.CaptureTransactionId = _invoice.Reference;
        });
        Reports(SdkData.PaidLightning());
        await CheckAsync();

        Assert.That(_shop.MarkedPaid, Is.Zero);
        Assert.That(_shop.NoteTexts, Has.None.EqualTo(_shop.Texts.Format("Note.PaidTwice", _invoice.Reference, Sats(24_500))));
        Assert.That(Stored.Final, Is.True);
    }

    private static IEnumerable<TestCaseData> CheckErrors()
    {
        yield return new TestCaseData(new NetworkException("down"), "network", "warning");
        yield return new TestCaseData(new RateException("no rate"), "rate", "warning");
        yield return new TestCaseData(new InvalidInvoiceException("damaged"), "invalid_invoice", "warning");
        yield return new TestCaseData(new RevokedException(), "revoked", "warning");
        yield return new TestCaseData(new ChannelNotFoundException(false), "channel_not_found", "warning");
        yield return new TestCaseData(new OperationCanceledException(), "timeout", "warning");
        yield return new TestCaseData(new InvalidOperationException("a bug"), "error", "error");
    }

    [TestCaseSource(nameof(CheckErrors))]
    public async Task A_failed_check_is_counted_and_done_again_later(Exception error, string code, string level)
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(30));
        _shop.Gateway.Status = (_, _) => throw error;
        await CheckAsync();

        Assert.That(Stored.Error, Is.EqualTo(code));
        Assert.That(Stored.Errors, Is.EqualTo(1));
        Assert.That(Stored.Final, Is.False);
        Assert.That(Stored.LeaseId, Is.Null);
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Logged(level), Has.Exactly(1).Contains(code));

        // Failing again: later each time, logged once.
        await CheckAsync();
        Assert.That(Stored.Errors, Is.EqualTo(2));
        Assert.That(Stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + (2 * SattlemeDefaults.OpenInterval)).Within(TimeSpan.FromSeconds(1)));
        Assert.That(_shop.Logged(level), Has.Exactly(1).Contains(code));
        Assert.That(_shop.MarkedPaid, Is.Zero);

        Reports(SdkData.Expired());
        await CheckAsync();
        Assert.That(Stored.Errors, Is.Zero);
        Assert.That(Stored.Error, Is.Empty);
        Assert.That(Stored.Final, Is.True);
    }

    [Test]
    public async Task Checks_at_most_every_few_seconds_for_the_payment_page()
    {
        var page = SattlemeDefaults.PageInterval;
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.True);
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.False);
        _shop.Time.Advance(page - TimeSpan.FromSeconds(1));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.False);
        _shop.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.That(await _shop.NewScope().Checker.CheckAsync(_invoice.Id, page, _timeout), Is.True);

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(2));
    }

    [Test]
    public async Task An_invoice_of_an_order_not_placed_yet_waits_for_it_then_is_logged()
    {
        var placed = await _shop.NewScope().Maker.MakeForPlacementAsync(new Nop.Services.Payments.ProcessPaymentRequest
        {
            OrderGuid = Guid.NewGuid(),
            OrderTotal = 24.50m,
            StoreId = 1,
            CustomerId = 7,
        });
        Reports(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(placed.Invoice!.Id, TimeSpan.Zero, _timeout);
        Assert.That(_shop.Database.Invoices().Single(invoice => invoice.Id == placed.Invoice.Id).Final, Is.False, "nopCommerce may still be storing the order");

        _shop.Time.Advance(PaymentChecker.PlacementWait + TimeSpan.FromSeconds(1));
        await _shop.NewScope().Checker.CheckAsync(placed.Invoice.Id, TimeSpan.Zero, _timeout);

        Assert.That(_shop.Database.Invoices().Single(invoice => invoice.Id == placed.Invoice.Id).Final, Is.True);
        Assert.That(_shop.Logged("error"), Has.Some.Contains(placed.Invoice.Reference).And.Some.Contains("no order"));
    }

    [Test]
    public async Task Never_checks_without_a_key()
    {
        _shop.Settings[0].EncryptedIntegrationKey = "";
        await CheckAsync();

        Assert.That(_shop.Gateway.Statuses, Is.Zero);
        Assert.That(Stored.Error, Is.EqualTo("no_key"));
    }
}