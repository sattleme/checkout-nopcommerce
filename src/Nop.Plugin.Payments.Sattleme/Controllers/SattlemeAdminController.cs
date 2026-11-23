using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Sattleme.Models;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Messages;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;
using NopSattleme.Sattleme.Checkout;

namespace Nop.Plugin.Payments.Sattleme.Controllers;

/// <summary>
/// The configuration page (Admin → Configuration → Payment methods → Bitcoin (sattle.me)): who may
/// manage payment methods sees it, every change posts its antiforgery token. The integration key is
/// write-only: verified when saved (the SDK refuses what is not an integration key, then reads the
/// integration on its relays: a removed one is refused), then never shown again, never sent back to
/// the browser, never logged.
/// </summary>
[AuthorizeAdmin]
[Area(AreaNames.ADMIN)]
[AutoValidateAntiforgeryToken]
public partial class SattlemeAdminController : BasePaymentController
{
    #region Fields

    private readonly ILanguageService _languageService;
    private readonly ILocalizationService _localizationService;
    private readonly ILogger _logger;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly INotificationService _notificationService;
    private readonly IPaymentPluginManager _paymentPluginManager;
    private readonly ISattlemeGateway _gateway;
    private readonly ISettingService _settingService;
    private readonly IStoreContext _storeContext;

    #endregion

    #region Ctor

    /// <summary>The controller.</summary>
    public SattlemeAdminController(ILanguageService languageService,
        ILocalizationService localizationService,
        ILogger logger,
        IntegrationKeys keys,
        InvoiceMaker maker,
        INotificationService notificationService,
        IPaymentPluginManager paymentPluginManager,
        ISattlemeGateway gateway,
        ISettingService settingService,
        IStoreContext storeContext)
    {
        _languageService = languageService;
        _localizationService = localizationService;
        _logger = logger;
        _keys = keys;
        _maker = maker;
        _notificationService = notificationService;
        _paymentPluginManager = paymentPluginManager;
        _gateway = gateway;
        _settingService = settingService;
        _storeContext = storeContext;
    }

    #endregion

    #region Methods

    /// <summary>The page, with the settings of the store chosen in the admin area (or every store's).</summary>
    [CheckPermission(StandardPermission.Configuration.MANAGE_PAYMENT_METHODS)]
    public async Task<IActionResult> Configure()
    {
        var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(storeScope);
        var paymentSettings = await _settingService.LoadSettingAsync<PaymentSettings>();
        var model = new ConfigurationModel
        {
            Enabled = paymentSettings.ActivePaymentMethodSystemNames.Contains(SattlemeDefaults.SYSTEM_NAME),
            MinutesToPay = settings.MinutesToPay,
            DescriptionText = settings.DescriptionText,
            AdditionalFee = settings.AdditionalFee,
            AdditionalFeePercentage = settings.AdditionalFeePercentage,
        };
        if (storeScope > 0)
        {
            model.MinutesToPay_OverrideForStore = await _settingService.SettingExistsAsync(settings, setting => setting.MinutesToPay, storeScope);
            model.DescriptionText_OverrideForStore = await _settingService.SettingExistsAsync(settings, setting => setting.DescriptionText, storeScope);
            model.AdditionalFee_OverrideForStore = await _settingService.SettingExistsAsync(settings, setting => setting.AdditionalFee, storeScope);
            model.AdditionalFeePercentage_OverrideForStore = await _settingService.SettingExistsAsync(settings, setting => setting.AdditionalFeePercentage, storeScope);
        }
        var plugin = await PluginAsync();
        foreach (var language in await _languageService.GetAllLanguagesAsync(showHidden: true))
        {
            model.Locales.Add(new ConfigurationLocalizedModel
            {
                LanguageId = language.Id,
                LanguageName = language.Name,
                Title = plugin is null ? null : await _localizationService.GetLocalizedFriendlyNameAsync(plugin, language.Id, returnDefaultValue: false),
                DescriptionText = await _localizationService.GetLocalizedSettingAsync(settings, setting => setting.DescriptionText, language.Id, 0, false, false),
            });
        }
        return View(SattlemeDefaults.VIEWS + "Configure.cshtml", await CompleteAsync(model, storeScope, settings));
    }

    /// <summary>Saves the settings: a key entered only once verified.</summary>
    [HttpPost]
    [CheckPermission(StandardPermission.Configuration.MANAGE_PAYMENT_METHODS)]
    public async Task<IActionResult> Configure(ConfigurationModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(storeScope);

        // The key entered never goes back to the browser, whatever happens below.
        var entered = model.IntegrationKey?.Trim();
        model.IntegrationKey = null;
        ModelState.Remove(nameof(model.IntegrationKey));

        if (model.MinutesToPay is < SattlemeDefaults.MIN_MINUTES or > SattlemeDefaults.MAX_MINUTES)
            ModelState.AddModelError(nameof(model.MinutesToPay), await ResourceAsync("MinutesToPay.Range"));
        string? business = null;
        if (!string.IsNullOrEmpty(entered))
        {
            var (verified, error) = await VerifyAsync(entered);
            if (error is not null)
                ModelState.AddModelError(nameof(model.IntegrationKey), error);
            business = verified;
        }
        if (!ModelState.IsValid)
            return View(SattlemeDefaults.VIEWS + "Configure.cshtml", await CompleteAsync(model, storeScope, settings));

        settings.MinutesToPay = model.MinutesToPay;
        settings.DescriptionText = model.DescriptionText ?? "";
        settings.AdditionalFee = model.AdditionalFee;
        settings.AdditionalFeePercentage = model.AdditionalFeePercentage;
        await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.MinutesToPay, model.MinutesToPay_OverrideForStore, storeScope, false);
        await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.DescriptionText, model.DescriptionText_OverrideForStore, storeScope, false);
        await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.AdditionalFee, model.AdditionalFee_OverrideForStore, storeScope, false);
        await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.AdditionalFeePercentage, model.AdditionalFeePercentage_OverrideForStore, storeScope, false);
        if (business is not null)
        {
            // A store's own key, or every store's.
            settings.EncryptedIntegrationKey = _keys.Protect(entered!);
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.EncryptedIntegrationKey, true, storeScope, false);
        }
        else if (model.RemoveKey)
        {
            // In a store: its own key goes (every store's then applies). For every store: it goes.
            settings.EncryptedIntegrationKey = "";
            await _settingService.SaveSettingOverridablePerStoreAsync(settings, setting => setting.EncryptedIntegrationKey, storeScope == 0, storeScope, false);
        }
        await _settingService.ClearCacheAsync();

        foreach (var locale in model.Locales)
        {
            await _localizationService.SaveLocalizedSettingAsync(settings, setting => setting.DescriptionText, locale.LanguageId, locale.DescriptionText ?? "");
            if (await PluginAsync() is { } plugin)
                await _localizationService.SaveLocalizedFriendlyNameAsync(plugin, locale.LanguageId, locale.Title ?? "");
        }

        // Offered or not: nopCommerce's own list of active payment methods (every store's).
        var paymentSettings = await _settingService.LoadSettingAsync<PaymentSettings>();
        var active = paymentSettings.ActivePaymentMethodSystemNames.Contains(SattlemeDefaults.SYSTEM_NAME);
        if (storeScope == 0 && model.Enabled != active)
        {
            if (model.Enabled && await _keys.KeyOfStoreAsync(0) is null)
                _notificationService.ErrorNotification(await ResourceAsync("Enabled.NeedsKey"));
            else
            {
                if (model.Enabled)
                    paymentSettings.ActivePaymentMethodSystemNames.Add(SattlemeDefaults.SYSTEM_NAME);
                else
                    paymentSettings.ActivePaymentMethodSystemNames.Remove(SattlemeDefaults.SYSTEM_NAME);
                await _settingService.SaveSettingAsync(paymentSettings);
            }
        }

        _notificationService.SuccessNotification(business is null
            ? await _localizationService.GetResourceAsync("Admin.Plugins.Saved")
            : string.Format(System.Globalization.CultureInfo.CurrentCulture, await ResourceAsync("Key.Verified"), business));
        // Shown again by a GET: nothing entered ever goes back to the browser.
        return RedirectToAction(nameof(Configure));
    }

    #endregion

    #region Utilities

    /// <summary>
    /// Checks a key the admin enters: an integration key (the SDK's construction), of an integration
    /// the merchant still has (its channel read on the relays). Gives the merchant's business name,
    /// or why the key is refused, in plain words.
    /// </summary>
    private async Task<(string? Business, string? Error)> VerifyAsync(string key)
    {
        ISattlemeClient client;
        try
        {
            client = _gateway.ClientOf(key);
        }
        catch (InvalidKeyException)
        {
            return (null, await ResourceAsync("Key.Invalid"));
        }

        try
        {
            using var timeout = new CancellationTokenSource(SattlemeDefaults.VerifyTimeout);
            var merchant = await client.GetMerchantAsync(timeout.Token);
            return (merchant.BusinessName, null);
        }
        catch (RevokedException)
        {
            return (null, await ResourceAsync("Key.Revoked"));
        }
        catch (ChannelNotFoundException notFound)
        {
            return (null, await ResourceAsync(notFound.NewerFormat ? "Key.Newer" : "Key.NotFound"));
        }
        catch (Exception exception)
        {
            await _logger.WarningAsync($"sattle.me: an integration key could not be verified: {PaymentRules.ErrorCode(exception)}: {exception.Message}");
            return (null, await ResourceAsync("Key.Unverified"));
        }
    }

    /// <summary>What the page says of the key and of the currency, and whether the method is offered.</summary>
    private async Task<ConfigurationModel> CompleteAsync(ConfigurationModel model, int storeScope, SattlemeSettings settings)
    {
        model.ActiveStoreScopeConfiguration = storeScope;
        // Offered or not is every store's setting: a store's page shows it as it is.
        if (storeScope > 0)
        {
            var paymentSettings = await _settingService.LoadSettingAsync<PaymentSettings>();
            model.Enabled = paymentSettings.ActivePaymentMethodSystemNames.Contains(SattlemeDefaults.SYSTEM_NAME);
        }
        var own = storeScope > 0 && await _settingService.SettingExistsAsync(settings, setting => setting.EncryptedIntegrationKey, storeScope);
        var saved = !string.IsNullOrEmpty(settings.EncryptedIntegrationKey);
        model.KeyStatus = !saved
            ? await ResourceAsync("Key.None")
            : _keys.KeyOf(settings) is null
                ? await ResourceAsync("Key.Unreadable")
                : await ResourceAsync(storeScope == 0 ? "Key.Saved" : own ? "Key.SavedForStore" : "Key.SavedForAll");
        if (await _maker.PriceOfAsync(1m) is null)
            model.CurrencyWarning = await ResourceAsync("Currency.Unsupported");
        var languages = await _languageService.GetAllLanguagesAsync(showHidden: true);
        foreach (var locale in model.Locales)
            locale.LanguageName = languages.FirstOrDefault(language => language.Id == locale.LanguageId)?.Name ?? "";
        return model;
    }

    private async Task<IPaymentMethod?> PluginAsync()
    {
        return await _paymentPluginManager.LoadPluginBySystemNameAsync(SattlemeDefaults.SYSTEM_NAME);
    }

    private Task<string> ResourceAsync(string name)
    {
        return _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + name);
    }

    #endregion
}