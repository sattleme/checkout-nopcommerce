using Nop.Core.Domain.Gdpr;
using Nop.Core.Domain.Localization;
using Nop.Core.Domain.Security;
using Nop.Core.Events;
using Nop.Services.Configuration;
using Nop.Services.Events;
using Nop.Services.Security;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// What the plugin does when nopCommerce publishes these events:
/// <list type="bullet">
/// <item>a customer's personal data removed (GdprService.PermanentDeleteCustomerAsync, which keeps
/// their orders): their invoices lose what leads to their coins;</item>
/// <item>a language created: it gets the plugin's texts;</item>
/// <item>the store's encryption key changed (Admin → Settings → Security, which re-encrypts
/// nopCommerce's own encrypted data): the integration keys saved are encrypted again with it.</item>
/// </list>
/// </summary>
public partial class EventConsumers :
    IConsumer<CustomerPermanentlyDeleted>,
    IConsumer<EntityInsertedEvent<Language>>,
    IConsumer<SecuritySettingsChangedEvent>
{
    #region Fields

    private readonly IEncryptionService _encryptionService;
    private readonly IntegrationKeys _keys;
    private readonly ISettingService _settingService;
    private readonly PersonalDataShredder _shredder;
    private readonly PluginResources _resources;

    #endregion

    #region Ctor

    /// <summary>The consumers.</summary>
    public EventConsumers(IEncryptionService encryptionService,
        IntegrationKeys keys,
        ISettingService settingService,
        PersonalDataShredder shredder,
        PluginResources resources)
    {
        _encryptionService = encryptionService;
        _keys = keys;
        _settingService = settingService;
        _shredder = shredder;
        _resources = resources;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public virtual async Task HandleEventAsync(CustomerPermanentlyDeleted eventMessage)
    {
        ArgumentNullException.ThrowIfNull(eventMessage);
        await _shredder.ShredCustomerAsync(eventMessage.CustomerId);
    }

    /// <inheritdoc/>
    public virtual async Task HandleEventAsync(EntityInsertedEvent<Language> eventMessage)
    {
        ArgumentNullException.ThrowIfNull(eventMessage);
        await _resources.InstallAsync(eventMessage.Entity);
    }

    /// <inheritdoc/>
    public virtual async Task HandleEventAsync(SecuritySettingsChangedEvent eventMessage)
    {
        ArgumentNullException.ThrowIfNull(eventMessage);
        var name = $"{nameof(SattlemeSettings)}.{nameof(SattlemeSettings.EncryptedIntegrationKey)}";
        var changed = false;
        foreach (var setting in await _settingService.GetAllSettingsAsync())
        {
            if (!setting.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            var key = _keys.Unprotect(setting.Value, eventMessage.OldEncryptionPrivateKey);
            if (key is null)
                continue;
            await _settingService.SetSettingAsync(setting.Name, _encryptionService.EncryptText(key, eventMessage.SecuritySettings.EncryptionKey), setting.StoreId, clearCache: false);
            changed = true;
        }
        if (changed)
            await _settingService.ClearCacheAsync();
    }

    #endregion
}