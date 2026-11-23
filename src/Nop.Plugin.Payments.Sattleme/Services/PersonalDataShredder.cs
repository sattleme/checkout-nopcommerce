using System.Text.Json.Nodes;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// Removes what leads to a customer's coins when nopCommerce removes the customer's personal data
/// (GdprService.PermanentDeleteCustomerAsync, which keeps their orders, then publishes
/// CustomerPermanentlyDeleted). A Bitcoin payment holds no name nor email, but an on-chain one is
/// public: the deposit address the customer paid, and the transactions that paid it, lead anyone to
/// the customer's coins and wallet on the blockchain. Those go from the invoices of the customer's
/// orders. What stays is not personal data on its own: the references, amounts, rates, dates and
/// states, the Lightning invoices (which name the merchant's node, never the customer's) and the
/// proofs of payment. An invoice whose final status is not known yet keeps what its checks need
/// until it is final, then loses it.
/// </summary>
public partial class PersonalDataShredder
{
    #region Fields

    /// <summary>What replaces a value removed.</summary>
    public const string SHREDDED = "█";

    private readonly SattlemeStore _store;

    #endregion

    #region Ctor

    /// <summary>The shredder of <paramref name="store"/>'s records.</summary>
    public PersonalDataShredder(SattlemeStore store)
    {
        _store = store;
    }

    #endregion

    #region Methods

    /// <summary>The customer <paramref name="customerId"/>'s personal data was removed: their invoices lose what leads to their coins.</summary>
    public virtual async Task ShredCustomerAsync(int customerId)
    {
        var orders = await _store.GetOrdersOfCustomerAsync(customerId);
        await _store.MarkShreddedAsync([.. orders.Select(order => order.OrderGuid)]);
        foreach (var order in orders)
        {
            foreach (var invoice in await _store.GetInvoicesAsync(order.OrderGuid))
                await ShredIfFinalAsync(invoice.Id);
        }
    }

    /// <summary>An invoice became final: if its customer's data was removed meanwhile, it loses what its checks needed.</summary>
    public virtual async Task ShredIfFinalAsync(int invoiceId)
    {
        var invoice = await _store.GetInvoiceAsync(invoiceId);
        if (invoice is null || !invoice.Final || !invoice.Shredded)
            return;
        await _store.ReplaceFinalDataAsync(invoice.Id, ShredInvoice(invoice.InvoiceJson), ShredStatus(invoice.StatusJson));
    }

    /// <summary>An invoice's JSON without its deposit address, which its payment URI (BIP-21) holds too.</summary>
    public static string? ShredInvoice(string? json)
    {
        if (json is null || JsonNode.Parse(json) is not JsonObject invoice)
            return json;
        if (invoice["onchainAddress"] is JsonValue)
        {
            invoice["onchainAddress"] = SHREDDED;
            invoice["paymentUri"] = SHREDDED;
        }
        return invoice.ToJsonString();
    }

    /// <summary>A status's JSON without the transactions that paid the invoice, nor the rest to pay (whose QR code holds the address).</summary>
    public static string? ShredStatus(string? json)
    {
        if (json is null || JsonNode.Parse(json) is not JsonObject status)
            return json;
        if (status["txids"] is JsonArray)
            status["txids"] = new JsonArray();
        if (status["rest"] is JsonObject)
            status["rest"] = null;
        return status.ToJsonString();
    }

    #endregion
}