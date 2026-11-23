using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Models;

/// <summary>What the order's page in the admin area shows of its Bitcoin invoices.</summary>
public partial record AdminOrderPaymentModel : BaseNopModel
{
    /// <summary>The invoices of the order, the latest first.</summary>
    public IList<AdminInvoiceModel> Invoices { get; set; } = [];

    /// <summary>A warning when an invoice may still be paid or is being confirmed: what cancelling the order would do.</summary>
    public string? Warning { get; set; }
}

/// <summary>One invoice of the order, for the admin: its reference, amounts, rate, address, transactions, proofs, checks.</summary>
public partial record AdminInvoiceModel : BaseNopModel
{
    /// <summary>The order reference the invoice was made for (the capture transaction's id once paid).</summary>
    public string Reference { get; set; } = "";

    /// <summary>What the merchant's sattle.me app shows of the sale.</summary>
    public string? Note { get; set; }

    /// <summary>Where its payment is, in plain words.</summary>
    public string State { get; set; } = "";

    /// <summary>The amount, in sats and BTC, and the price it was converted from.</summary>
    public string Amount { get; set; } = "";

    /// <summary>The rate, its source and when it was read.</summary>
    public string? Rate { get; set; }

    /// <summary>What came, in sats.</summary>
    public string? Received { get; set; }

    /// <summary>Confirmations of an on-chain payment being confirmed.</summary>
    public int? Confirmations { get; set; }

    /// <summary>The deposit address (a link to mempool.space when it is one).</summary>
    public string? Address { get; set; }

    /// <summary>Whether <see cref="Address"/> is an address, linked to mempool.space.</summary>
    public bool AddressLink { get; set; }

    /// <summary>The transactions that paid it (linked to mempool.space).</summary>
    public IList<string> Txids { get; set; } = [];

    /// <summary>The proofs of its Lightning payments (preimages).</summary>
    public IList<string> Preimages { get; set; } = [];

    /// <summary>When it was made, when it ends, when it was last checked and is checked next (admin's time zone).</summary>
    public string? Created { get; set; }

    /// <inheritdoc cref="Created"/>
    public string? Expires { get; set; }

    /// <inheritdoc cref="Created"/>
    public string? LastCheck { get; set; }

    /// <inheritdoc cref="Created"/>
    public string? NextCheck { get; set; }

    /// <summary>The last error, in plain words.</summary>
    public string? Error { get; set; }

    /// <summary>Whether its customer's personal data was removed.</summary>
    public bool Shredded { get; set; }
}