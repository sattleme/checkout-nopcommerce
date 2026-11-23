using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.ScheduleTasks;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The background checks (README.md, "Background checks"): the SDK has no webhooks, so nopCommerce's
/// scheduler runs this task every minute. Each run checks the invoices whose check is due when it
/// starts, the longest due first (PaymentRules.NextCheck says when, counted from the run's start:
/// at every run while an invoice can be paid, around the end of its grace period, then every fifth
/// run until its final status is known, less often after failures), at most 20 and for about 45
/// seconds, then gives up on the invoices still unsettled 90 days after they were made, with a note
/// on their order.
/// </summary>
public partial class SattlemeCheckTask : IScheduleTask
{
    #region Fields

    private readonly ILogger _logger;
    private readonly IOrderService _orderService;
    private readonly OrderNotes _notes;
    private readonly PaymentChecker _checker;
    private readonly PersonalDataShredder _shredder;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;

    #endregion

    #region Ctor

    /// <summary>The task.</summary>
    public SattlemeCheckTask(ILogger logger,
        IOrderService orderService,
        OrderNotes notes,
        PaymentChecker checker,
        PersonalDataShredder shredder,
        SattlemeStore store,
        TimeProvider time)
    {
        _logger = logger;
        _orderService = orderService;
        _notes = notes;
        _checker = checker;
        _shredder = shredder;
        _store = store;
        _time = time;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public virtual async Task ExecuteAsync()
    {
        var started = _time.GetTimestamp();
        var now = _time.GetUtcNow().UtcDateTime;

        foreach (var invoice in await _store.GetDueAsync(now, SattlemeDefaults.TASK_BATCH))
        {
            // A check that could end after the run's budget waits for the next run: the others first.
            if (SattlemeDefaults.TaskBudget - _time.GetElapsedTime(started) < SattlemeDefaults.CheckTimeout)
                break;
            // Skipped when the payment page's check holds it or just ended: due again at the next run.
            if (!await _checker.CheckAsync(invoice.Id, SattlemeDefaults.PageInterval, SattlemeDefaults.CheckTimeout, now))
                await _store.SetNextCheckAsync(invoice.Id, now - SattlemeDefaults.RunSlack + SattlemeDefaults.OpenInterval);
        }

        foreach (var invoice in await _store.GetUnsettledMadeBeforeAsync(now - SattlemeDefaults.GiveUpAfter, SattlemeDefaults.TASK_BATCH))
        {
            if (!await _store.GiveUpAsync(invoice.Id))
                continue;
            await _shredder.ShredIfFinalAsync(invoice.Id);
            await _logger.WarningAsync($"sattle.me: the Bitcoin invoice {invoice.Reference} had no final status after {SattlemeDefaults.GiveUpAfter.Days} days: its checks stopped");
            var order = await _orderService.GetOrderByGuidAsync(invoice.OrderGuid);
            if (order is not null)
                await _notes.AddAsync(order.Id, "Note.Abandoned", invoice.Reference, SattlemeDefaults.GiveUpAfter.Days);
        }
    }

    #endregion
}