using Nop.Core.Configuration;

namespace Nop.Plugin.Payments.Sattleme;

/// <summary>
/// The plugin's settings, in nopCommerce's settings store, per store where the admin overrides
/// them (as nopCommerce's own payment methods). Whether the method is offered is nopCommerce's own
/// list of active payment methods (PaymentSettings.ActivePaymentMethodSystemNames).
/// </summary>
public partial class SattlemeSettings : ISettings
{
    /// <summary>
    /// The integration's secret key (<c>sattleme_ck_…</c>), encrypted with nopCommerce's encryption
    /// service (IEncryptionService, keyed by the store's encryption key): never shown again once
    /// saved, never sent to the browser. Empty when none.
    /// </summary>
    public string EncryptedIntegrationKey { get; set; } = "";

    /// <summary>How many minutes the customer has to pay an invoice: 1 to 60.</summary>
    public int MinutesToPay { get; set; } = SattlemeDefaults.DEFAULT_MINUTES;

    /// <summary>
    /// What the checkout says of the method (localizable, per language); empty: how a Bitcoin
    /// payment goes, in the customer's language.
    /// </summary>
    public string DescriptionText { get; set; } = "";

    /// <summary>An additional fee, as nopCommerce's own payment methods charge one.</summary>
    public decimal AdditionalFee { get; set; }

    /// <summary>Whether <see cref="AdditionalFee"/> is a percentage of the order's total.</summary>
    public bool AdditionalFeePercentage { get; set; }
}