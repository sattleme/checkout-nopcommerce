using System.ComponentModel.DataAnnotations;
using Nop.Web.Framework.Models;
using Nop.Web.Framework.Mvc.ModelBinding;

namespace Nop.Plugin.Payments.Sattleme.Models;

/// <summary>
/// The configuration page. The integration key is write-only: the page never holds the saved one
/// (it says whether one is saved), and a key entered is never sent back to the browser.
/// </summary>
public partial record ConfigurationModel : BaseNopModel, ILocalizedModel<ConfigurationLocalizedModel>
{
    /// <summary>The store whose settings are shown (0: every store's).</summary>
    public int ActiveStoreScopeConfiguration { get; set; }

    /// <summary>Whether the payment method is offered (nopCommerce's list of active payment methods).</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.Enabled")]
    public bool Enabled { get; set; }

    /// <summary>A new integration key, as entered; null on the way to the browser.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.IntegrationKey")]
    [DataType(DataType.Password)]
    public string? IntegrationKey { get; set; }

    /// <summary>Whether a key is saved for the store shown (one of its own, or the default one), and whether it can be read.</summary>
    public string KeyStatus { get; set; } = "";

    /// <summary>Whether to remove the key saved for the store shown.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.RemoveKey")]
    public bool RemoveKey { get; set; }

    /// <summary>How many minutes the customer has to pay an invoice.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.MinutesToPay")]
    public int MinutesToPay { get; set; }

    /// <summary>Whether the store shown has its own value.</summary>
    public bool MinutesToPay_OverrideForStore { get; set; }

    /// <summary>The description at checkout, in the default language.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.DescriptionText")]
    public string? DescriptionText { get; set; }

    /// <inheritdoc cref="MinutesToPay_OverrideForStore"/>
    public bool DescriptionText_OverrideForStore { get; set; }

    /// <summary>The additional fee.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.AdditionalFee")]
    public decimal AdditionalFee { get; set; }

    /// <inheritdoc cref="MinutesToPay_OverrideForStore"/>
    public bool AdditionalFee_OverrideForStore { get; set; }

    /// <summary>Whether the additional fee is a percentage of the order's total.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.AdditionalFeePercentage")]
    public bool AdditionalFeePercentage { get; set; }

    /// <inheritdoc cref="MinutesToPay_OverrideForStore"/>
    public bool AdditionalFeePercentage_OverrideForStore { get; set; }

    /// <summary>When the store's primary currency is not one the SDK converts: why the method is not offered.</summary>
    public string? CurrencyWarning { get; set; }

    /// <summary>The name and description of the method at checkout, per language.</summary>
    public IList<ConfigurationLocalizedModel> Locales { get; set; } = [];
}

/// <summary>The name and description of the method at checkout, in one language.</summary>
public partial record ConfigurationLocalizedModel : ILocalizedLocaleModel
{
    /// <summary>The language.</summary>
    public int LanguageId { get; set; }

    /// <summary>Its name, for the form's labels.</summary>
    public string LanguageName { get; set; } = "";

    /// <summary>The method's name at checkout (nopCommerce's localized name of the plugin).</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.Title")]
    public string? Title { get; set; }

    /// <summary>The description at checkout.</summary>
    [NopResourceDisplayName("Plugins.Payments.Sattleme.Fields.DescriptionText")]
    public string? DescriptionText { get; set; }
}