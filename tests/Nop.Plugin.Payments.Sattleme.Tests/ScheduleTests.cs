using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>The background checks: when each invoice is checked, how many a run takes, and the 90 days.</summary>
[TestFixture]
public sealed class ScheduleTests
{
    private static readonly DateTime _now = new(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly long _nowUnix = new DateTimeOffset(_now).ToUnixTimeSeconds();
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

    [Test]
    public void Checks_every_minute_while_the_invoice_can_be_paid()
    {
        var expiresAt = _nowUnix + 600;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(_now.AddMinutes(1)));
    }

    [Test]
    public void Checks_just_before_and_just_after_the_end_of_the_grace_period()
    {
        // The grace period ends 120 s after the invoice: checked 10 s before, then 10 s after.
        var expiresAt = _nowUnix - 100;
        var end = _now.AddSeconds(20);
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(end - SattlemeDefaults.EndMargin));
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, end - SattlemeDefaults.EndMargin), Is.EqualTo(end + SattlemeDefaults.EndMargin));
    }

    [Test]
    public void Waits_at_least_a_few_seconds_between_two_checks()
    {
        var expiresAt = _nowUnix - 115;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now) - _now, Is.GreaterThanOrEqualTo(SattlemeDefaults.PageInterval));
    }

    [Test]
    public void Checks_every_five_minutes_until_the_status_is_final()
    {
        var expiresAt = _nowUnix - 3600;
        Assert.That(PaymentRules.NextCheck(expiresAt, 0, _now), Is.EqualTo(_now.AddMinutes(5)));
    }

    [TestCase(1, 1)]
    [TestCase(2, 2)]
    [TestCase(3, 4)]
    [TestCase(5, 16)]
    [TestCase(6, 30)]
    [TestCase(40, 30)]
    public void Checks_less_often_after_failures(int errors, int minutes)
    {
        var expiresAt = _nowUnix - 3600;
        Assert.That(PaymentRules.NextCheck(expiresAt, errors, _now), Is.EqualTo(_now.AddMinutes(minutes)));
    }

    private async Task<SattlemeInvoice> MakeAsync()
    {
        var order = _shop.AddOrder();
        return (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
    }

    [Test]
    public async Task A_run_checks_the_invoices_due_the_longest_due_first()
    {
        var first = await MakeAsync();
        var second = await MakeAsync();
        var later = await MakeAsync();
        _shop.Database.Change(first.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(-30));
        _shop.Database.Change(second.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(-90));
        _shop.Database.Change(later.Id, invoice => invoice.NextCheckOnUtc = _shop.Time.UtcNow.AddSeconds(30));
        var checkedReferences = new List<string>();
        _shop.Gateway.Status = (invoice, _) =>
        {
            checkedReferences.Add(invoice.Ref!);
            return Task.FromResult(SdkData.Open(invoice.ExpiresAt));
        };

        await _shop.NewScope().Task.ExecuteAsync();

        Assert.That(checkedReferences, Is.EqualTo(new[] { second.Reference, first.Reference }));
        // Due again a minute after the run's start, less its slack: at the next run.
        var stored = _shop.Database.Invoices().Single(invoice => invoice.Id == first.Id);
        Assert.That(stored.NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval - SattlemeDefaults.RunSlack).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public void Counts_a_run_s_checks_from_the_run_s_start()
    {
        // A check that ended 8 s after its run started: due again a period after the run's start,
        // less the slack, so that the next run (a minute after this one) checks it.
        var run = _now.AddSeconds(-8);
        var slack = SattlemeDefaults.RunSlack;
        Assert.That(PaymentRules.NextCheck(_nowUnix + 600, 0, _now, run), Is.EqualTo(run.AddMinutes(1) - slack));
        Assert.That(PaymentRules.NextCheck(_nowUnix - 3600, 0, _now, run), Is.EqualTo(run.AddMinutes(5) - slack));
        Assert.That(PaymentRules.NextCheck(_nowUnix - 3600, 3, _now, run), Is.EqualTo(run.AddMinutes(4) - slack));
        // The end of the grace period still decides the checks around it.
        var end = _now.AddSeconds(20);
        Assert.That(PaymentRules.NextCheck(_nowUnix - 100, 0, _now, run), Is.EqualTo(end - SattlemeDefaults.EndMargin));
        // Never sooner than a few seconds after the check.
        Assert.That(PaymentRules.NextCheck(_nowUnix - 3600, 0, _now, _now.AddMinutes(-10)) - _now, Is.GreaterThanOrEqualTo(SattlemeDefaults.PageInterval));
    }

    [Test]
    public async Task A_run_checks_an_invoice_at_every_run_while_it_can_be_paid_then_at_every_fifth_run()
    {
        // nopCommerce's scheduler: a run a minute, each starting up to 2 s sooner or later; each
        // check takes 3 s. The invoice can be paid for 15 minutes, its payment reported up to 2
        // minutes later (its grace period); then an on-chain payment is being confirmed.
        var invoice = await MakeAsync();
        var start = _shop.Time.UtcNow;
        var run = 0;
        var checkedAt = new List<int>();
        _shop.Gateway.Status = (checkedInvoice, _) =>
        {
            checkedAt.Add(run);
            _shop.Time.Advance(TimeSpan.FromSeconds(3));
            return Task.FromResult(_shop.Time.UnixNow < checkedInvoice.ExpiresAt + SattlemeDefaults.GRACE_SECONDS
                ? SdkData.Open(checkedInvoice.ExpiresAt)
                : SdkData.Unconfirmed(24_500, 0));
        };
        for (run = 1; run <= 40; run++)
        {
            _shop.Time.Advance(start + TimeSpan.FromSeconds((60 * run) + (run % 2 == 0 ? 2 : -2)) - _shop.Time.UtcNow);
            await _shop.NewScope().Task.ExecuteAsync();
        }

        // Every run until the end of its grace period (17 minutes: runs 1 to 17), the first one
        // after it (its final status: run 18), then every fifth run (every five minutes).
        Assert.That(checkedAt, Is.EqualTo(Enumerable.Range(1, 18).Concat([23, 28, 33, 38])));
        Assert.That(_shop.Database.Invoices().Single(stored => stored.Id == invoice.Id).State, Is.EqualTo("unconfirmed"));
    }

    [Test]
    public async Task A_run_checks_at_most_twenty_invoices()
    {
        for (var i = 0; i < SattlemeDefaults.TASK_BATCH + 3; i++)
            await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));

        await _shop.NewScope().Task.ExecuteAsync();

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(SattlemeDefaults.TASK_BATCH));
        await _shop.NewScope().Task.ExecuteAsync();
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(SattlemeDefaults.TASK_BATCH + 3));
    }

    [Test]
    public async Task A_run_stops_checking_before_its_time_is_over()
    {
        for (var i = 0; i < 4; i++)
            await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        // Each check takes 9 s: the run stops once another one could end after its budget (45 s,
        // a check 20 s at most): after the third.
        _shop.Gateway.Status = (invoice, _) =>
        {
            _shop.Time.Advance(TimeSpan.FromSeconds(9));
            return Task.FromResult(SdkData.Open(invoice.ExpiresAt));
        };

        await _shop.NewScope().Task.ExecuteAsync();

        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(3));
    }

    [Test]
    public async Task A_check_the_page_holds_is_left_for_a_minute()
    {
        var invoice = await MakeAsync();
        _shop.Time.Advance(TimeSpan.FromMinutes(1));
        _shop.Database.Change(invoice.Id, stored =>
        {
            stored.LeaseId = "the page's";
            stored.LeaseUntilUtc = _shop.Time.UtcNow.AddSeconds(30);
        });

        await _shop.NewScope().Task.ExecuteAsync();

        Assert.That(_shop.Gateway.Statuses, Is.Zero);
        Assert.That(_shop.Database.Invoices().Single().NextCheckOnUtc, Is.EqualTo(_shop.Time.UtcNow + SattlemeDefaults.OpenInterval - SattlemeDefaults.RunSlack).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Gives_up_after_90_days_with_a_note()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 0));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _shop.Time.Advance(TimeSpan.FromDays(89));
        await _shop.NewScope().Task.ExecuteAsync();
        Assert.That(_shop.Database.Invoices().Single().Final, Is.False);

        _shop.Time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromMinutes(1));
        _shop.Gateway.Status = (_, _) => throw new NetworkException("down");
        await _shop.NewScope().Task.ExecuteAsync();

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Final, Is.True);
        Assert.That(stored.Error, Is.EqualTo("abandoned"));
        Assert.That(stored.NextCheckOnUtc, Is.Null);
        Assert.That(_shop.NoteTexts, Has.Some.EqualTo(_shop.Texts.Format("Note.Abandoned", invoice.Reference, 90)));
        Assert.That(_shop.Logged("warning"), Has.Some.Contains(invoice.Reference).And.Some.Contains("90 days"));
        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(Core.Domain.Payments.PaymentStatus.Pending));
        var statuses = _shop.Gateway.Statuses;
        await _shop.NewScope().Task.ExecuteAsync();
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(statuses), "never checked again");
    }

    [Test]
    public void The_task_is_installed_to_run_every_minute()
    {
        Assert.That(SattlemeDefaults.CHECK_TASK_SECONDS, Is.EqualTo(60));
        Assert.That(Type.GetType(SattlemeDefaults.CHECK_TASK_TYPE), Is.EqualTo(typeof(SattlemeCheckTask)));
        // nopCommerce holds a task's lock for its period less one second (ScheduleTaskRunner): a run fits in it.
        Assert.That(SattlemeDefaults.TaskBudget, Is.LessThan(TimeSpan.FromSeconds(SattlemeDefaults.CHECK_TASK_SECONDS - 1)));
        Assert.That(SattlemeDefaults.CheckTimeout + SattlemeDefaults.LeaseMargin, Is.LessThan(TimeSpan.FromSeconds(SattlemeDefaults.CHECK_TASK_SECONDS)));
    }
}