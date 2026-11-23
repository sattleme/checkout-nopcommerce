using Nop.Web.Framework.Models;

namespace Nop.Plugin.Payments.Sattleme.Models;

/// <summary>What the order's page in the shop says of its Bitcoin payment.</summary>
public partial record OrderPaymentModel : BaseNopModel
{
    /// <summary>Where the payment is (PaymentPhase, in lower case).</summary>
    public string Phase { get; set; } = "";

    /// <summary>The payment page.</summary>
    public string PayUrl { get; set; } = "";

    /// <summary>When the invoice ends, in the customer's time zone.</summary>
    public string? Until { get; set; }

    /// <summary>Whether something came for the invoice (an underpayment that ended: what came, of its sats).</summary>
    public bool HasReceived { get; set; }

    /// <inheritdoc cref="HasReceived"/>
    public string ReceivedSats { get; set; } = "";

    /// <inheritdoc cref="HasReceived"/>
    public string Sats { get; set; } = "";

    /// <summary>Whether the block links to the payment page: while it shows more than the block (an invoice, a payment's progress, a new invoice).</summary>
    public bool ShowPayLink { get; set; }
}