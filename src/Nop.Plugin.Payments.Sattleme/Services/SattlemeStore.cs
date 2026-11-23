using System.Security.Cryptography;
using LinqToDB;
using LinqToDB.Async;
using Nop.Data;
using Nop.Plugin.Payments.Sattleme.Domain;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The plugin's records in the shop's database. Every lock and every claim is one conditional
/// UPDATE (it changes the row only when the condition holds, and says whether it did), atomic on
/// every database nopCommerce runs on: two requests, or two servers, never both hold the order's
/// lock, the check of an invoice, or its completion. (linq2db's asynchronous queries are called by
/// their class, LinqToDB.Async.AsyncExtensions: nopCommerce has extensions of the same names.)
/// </summary>
public partial class SattlemeStore
{
    #region Fields

    private readonly INopDataProvider _dataProvider;

    #endregion

    #region Ctor

    /// <summary>The records of the database <paramref name="dataProvider"/> reaches.</summary>
    public SattlemeStore(INopDataProvider dataProvider)
    {
        _dataProvider = dataProvider;
    }

    #endregion

    #region Orders

    /// <summary>The record of the order <paramref name="orderGuid"/>, or null.</summary>
    public virtual async Task<SattlemeOrder?> GetOrderAsync(Guid orderGuid)
    {
        return await AsyncExtensions.FirstOrDefaultAsync(_dataProvider.GetTable<SattlemeOrder>()
            .Where(order => order.OrderGuid == orderGuid));
    }

    /// <summary>The records of the orders of <paramref name="customerId"/>.</summary>
    public virtual async Task<IList<SattlemeOrder>> GetOrdersOfCustomerAsync(int customerId)
    {
        return await _dataProvider.GetTable<SattlemeOrder>()
            .Where(order => order.CustomerId == customerId)
            .ToListAsync();
    }

    /// <summary>
    /// The record of the order <paramref name="orderGuid"/>, made (with the secret of its payment
    /// page) when it has none. Two requests making it at once get the same one.
    /// </summary>
    public virtual async Task<SattlemeOrder> GetOrMakeOrderAsync(Guid orderGuid, int storeId, int customerId, DateTime now)
    {
        var existing = await GetOrderAsync(orderGuid);
        if (existing is not null)
            return existing;

        var made = new SattlemeOrder
        {
            OrderGuid = orderGuid,
            StoreId = storeId,
            CustomerId = customerId,
            PageToken = NewSecret(32),
            CreatedOnUtc = now,
        };
        try
        {
            await _dataProvider.InsertEntityAsync(made);
            return made;
        }
        catch (Exception)
        {
            // Another request made it first (the GUID is unique): that one.
            return await GetOrderAsync(orderGuid) ?? throw new InvalidOperationException("The order's record could not be made");
        }
    }

    /// <summary>Records the identifier nopCommerce gave the order <paramref name="orderGuid"/>.</summary>
    public virtual async Task SetOrderIdAsync(Guid orderGuid, int orderId)
    {
        await _dataProvider.GetTable<SattlemeOrder>()
            .Where(order => order.OrderGuid == orderGuid && order.OrderId != orderId)
            .Set(order => order.OrderId, orderId)
            .UpdateAsync();
    }

    /// <summary>
    /// Takes the lock of the order <paramref name="orderGuid"/> for <paramref name="lockId"/> until
    /// <paramref name="until"/>, unless another holder has it (until a time after <paramref name="now"/>).
    /// </summary>
    public virtual async Task<bool> TryLockOrderAsync(Guid orderGuid, string lockId, DateTime now, DateTime until)
    {
        return await _dataProvider.GetTable<SattlemeOrder>()
            .Where(order => order.OrderGuid == orderGuid && (order.LockedUntilUtc == null || order.LockedUntilUtc < now))
            .Set(order => order.LockId, lockId)
            .Set(order => order.LockedUntilUtc, until)
            .UpdateAsync() == 1;
    }

    /// <summary>Releases the lock <paramref name="lockId"/> holds on the order (nothing when it no longer holds it).</summary>
    public virtual async Task UnlockOrderAsync(Guid orderGuid, string lockId)
    {
        await _dataProvider.GetTable<SattlemeOrder>()
            .Where(order => order.OrderGuid == orderGuid && order.LockId == lockId)
            .Set(order => order.LockId, (string?)null)
            .Set(order => order.LockedUntilUtc, (DateTime?)null)
            .UpdateAsync();
    }

    #endregion

    #region Invoices

    /// <summary>The record <paramref name="id"/>, or null.</summary>
    public virtual async Task<SattlemeInvoice?> GetInvoiceAsync(int id)
    {
        return await AsyncExtensions.FirstOrDefaultAsync(_dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id));
    }

    /// <summary>The invoices of the order <paramref name="orderGuid"/>, oldest first.</summary>
    public virtual async Task<IList<SattlemeInvoice>> GetInvoicesAsync(Guid orderGuid)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.OrderGuid == orderGuid)
            .OrderBy(invoice => invoice.Id)
            .ToListAsync();
    }

    /// <summary>Stores a new try of an order, its reference made: before the SDK is asked for an invoice of it.</summary>
    public virtual async Task InsertInvoiceAsync(SattlemeInvoice invoice)
    {
        await _dataProvider.InsertEntityAsync(invoice);
    }

    /// <summary>
    /// Stores the invoice the SDK made for the try <paramref name="id"/>, unless it has one: its
    /// checks start at <paramref name="nextCheck"/>.
    /// </summary>
    public virtual async Task<bool> StoreMadeInvoiceAsync(int id, string invoiceJson, long expiresAt, DateTime nextCheck)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.InvoiceJson == null)
            .Set(invoice => invoice.InvoiceJson, invoiceJson)
            .Set(invoice => invoice.ExpiresAt, expiresAt)
            .Set(invoice => invoice.NextCheckOnUtc, nextCheck)
            .Set(invoice => invoice.Error, "")
            .UpdateAsync() == 1;
    }

    /// <summary>Why the try <paramref name="id"/> made no invoice (an error's code).</summary>
    public virtual async Task SetMakeErrorAsync(int id, string code)
    {
        await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.InvoiceJson == null)
            .Set(invoice => invoice.Error, code)
            .UpdateAsync();
    }

    /// <summary>
    /// Takes the check of the invoice <paramref name="id"/> for <paramref name="leaseId"/> until
    /// <paramref name="until"/>: only when it is made and not final, no other check holds it, and,
    /// with <paramref name="checkedBefore"/>, no check ended after that.
    /// </summary>
    public virtual async Task<bool> TryLeaseCheckAsync(int id, string leaseId, DateTime now, DateTime until, DateTime? checkedBefore)
    {
        var query = _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && !invoice.Final && invoice.InvoiceJson != null
                && (invoice.LeaseUntilUtc == null || invoice.LeaseUntilUtc < now));
        if (checkedBefore is { } before)
            query = query.Where(invoice => invoice.CheckedOnUtc == null || invoice.CheckedOnUtc <= before);
        return await query
            .Set(invoice => invoice.LeaseId, leaseId)
            .Set(invoice => invoice.LeaseUntilUtc, until)
            .UpdateAsync() == 1;
    }

    /// <summary>
    /// Stores what the check holding <paramref name="leaseId"/> found; false when it no longer holds
    /// the invoice (its lease ended and another check took it): it then decides nothing.
    /// </summary>
    public virtual async Task<bool> StoreStatusAsync(int id, string leaseId, string statusJson, string state, long received, DateTime now)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId && !invoice.Final)
            .Set(invoice => invoice.StatusJson, statusJson)
            .Set(invoice => invoice.State, state)
            .Set(invoice => invoice.Received, received)
            .Set(invoice => invoice.CheckedOnUtc, now)
            .Set(invoice => invoice.Error, "")
            .UpdateAsync() == 1;
    }

    /// <summary>A check holding <paramref name="leaseId"/> failed with <paramref name="code"/>: one more failure in a row.</summary>
    public virtual async Task FailCheckAsync(int id, string leaseId, string code, DateTime now)
    {
        await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId)
            .Set(invoice => invoice.Error, code)
            .Set(invoice => invoice.Errors, invoice => invoice.Errors + 1)
            .Set(invoice => invoice.CheckedOnUtc, now)
            .Set(invoice => invoice.LeaseId, (string?)null)
            .Set(invoice => invoice.LeaseUntilUtc, (DateTime?)null)
            .UpdateAsync();
    }

    /// <summary>
    /// Takes the completion of the paid invoice <paramref name="id"/> (marking its order paid) for
    /// the check holding <paramref name="leaseId"/>: unless one was taken after
    /// <paramref name="staleBefore"/> (it may still be under way).
    /// </summary>
    public virtual async Task<bool> ClaimCompletionAsync(int id, string leaseId, DateTime now, DateTime staleBefore)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId && !invoice.Final
                && (invoice.CompletionClaimedOnUtc == null || invoice.CompletionClaimedOnUtc < staleBefore))
            .Set(invoice => invoice.CompletionClaimedOnUtc, now)
            .UpdateAsync() == 1;
    }

    /// <summary>
    /// Ends the check holding <paramref name="leaseId"/>: the invoice final (never checked again),
    /// or its next background check at <paramref name="nextCheck"/>; its failures in a row reset.
    /// </summary>
    public virtual async Task ReleaseCheckAsync(int id, string leaseId, bool final, DateTime? nextCheck)
    {
        var held = _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.LeaseId == leaseId);
        var update = final
            ? held.Set(invoice => invoice.Final, true).Set(invoice => invoice.NextCheckOnUtc, (DateTime?)null)
            : held.Set(invoice => invoice.NextCheckOnUtc, nextCheck);
        await update
            .Set(invoice => invoice.Errors, 0)
            .Set(invoice => invoice.LeaseId, (string?)null)
            .Set(invoice => invoice.LeaseUntilUtc, (DateTime?)null)
            .UpdateAsync();
    }

    /// <summary>When the background checks look at the invoice <paramref name="id"/> next (unless it is final).</summary>
    public virtual async Task SetNextCheckAsync(int id, DateTime nextCheck)
    {
        await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && !invoice.Final)
            .Set(invoice => invoice.NextCheckOnUtc, nextCheck)
            .UpdateAsync();
    }

    /// <summary>The invoices whose background check is due at <paramref name="now"/>, the longest due first.</summary>
    public virtual async Task<IList<SattlemeInvoice>> GetDueAsync(DateTime now, int take)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => !invoice.Final && invoice.InvoiceJson != null && invoice.NextCheckOnUtc != null && invoice.NextCheckOnUtc <= now)
            .OrderBy(invoice => invoice.NextCheckOnUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>The invoices made before <paramref name="madeBefore"/> whose final status is still unknown.</summary>
    public virtual async Task<IList<SattlemeInvoice>> GetUnsettledMadeBeforeAsync(DateTime madeBefore, int take)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => !invoice.Final && invoice.InvoiceJson != null && invoice.CreatedOnUtc < madeBefore)
            .OrderBy(invoice => invoice.CreatedOnUtc)
            .Take(take)
            .ToListAsync();
    }

    /// <summary>How many invoices are made and not final: a payment may still come for each.</summary>
    public virtual async Task<int> CountUnsettledAsync()
    {
        return await AsyncExtensions.CountAsync(_dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => !invoice.Final && invoice.InvoiceJson != null));
    }

    /// <summary>Gives up on the invoice <paramref name="id"/> (no final status after 90 days); false when it was final already.</summary>
    public virtual async Task<bool> GiveUpAsync(int id)
    {
        return await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && !invoice.Final)
            .Set(invoice => invoice.Final, true)
            .Set(invoice => invoice.Error, "abandoned")
            .Set(invoice => invoice.NextCheckOnUtc, (DateTime?)null)
            .UpdateAsync() == 1;
    }

    #endregion

    #region Personal data

    /// <summary>Marks the invoices of the orders <paramref name="orderGuids"/> as their customer's data removed.</summary>
    public virtual async Task MarkShreddedAsync(IList<Guid> orderGuids)
    {
        if (orderGuids.Count == 0)
            return;
        await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => orderGuids.Contains(invoice.OrderGuid))
            .Set(invoice => invoice.Shredded, true)
            .UpdateAsync();
    }

    /// <summary>Replaces what a final invoice keeps (its JSON and its last status) with what is left once shredded.</summary>
    public virtual async Task ReplaceFinalDataAsync(int id, string? invoiceJson, string? statusJson)
    {
        await _dataProvider.GetTable<SattlemeInvoice>()
            .Where(invoice => invoice.Id == id && invoice.Final && invoice.Shredded)
            .Set(invoice => invoice.InvoiceJson, invoiceJson)
            .Set(invoice => invoice.StatusJson, statusJson)
            .UpdateAsync();
    }

    #endregion

    #region Utilities

    /// <summary><paramref name="bytes"/> bytes of the operating system's CSPRNG, in hex.</summary>
    public static string NewSecret(int bytes)
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    }

    #endregion
}