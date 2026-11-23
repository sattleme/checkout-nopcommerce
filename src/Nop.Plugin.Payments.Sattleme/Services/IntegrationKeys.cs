using Nop.Services.Configuration;
using Nop.Services.Security;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The integration key at rest: encrypted with nopCommerce's encryption service (keyed by the
/// store's encryption key, SecuritySettings.EncryptionKey) in the settings store, decrypted only
/// to make the SDK's client of a store. It never leaves the server: no page, no model, no log.
/// </summary>
public partial class IntegrationKeys
{
    #region Fields

    /// <summary>What every integration key starts with.</summary>
    public const string KEY_PREFIX = "sattleme_ck_";

    private readonly IEncryptionService _encryptionService;
    private readonly ISettingService _settingService;

    #endregion

    #region Ctor

    /// <summary>The keys, encrypted with <paramref name="encryptionService"/>.</summary>
    public IntegrationKeys(IEncryptionService encryptionService, ISettingService settingService)
    {
        _encryptionService = encryptionService;
        _settingService = settingService;
    }

    #endregion

    #region Methods

    /// <summary>What the settings store keeps of <paramref name="key"/>.</summary>
    public virtual string Protect(string key)
    {
        return _encryptionService.EncryptText(key);
    }

    /// <summary>
    /// The key <paramref name="settings"/> hold, or null when they hold none, or one that cannot be
    /// read (encrypted with another encryption key, damaged): the admin enters it again.
    /// </summary>
    public virtual string? KeyOf(SattlemeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Unprotect(settings.EncryptedIntegrationKey, "");
    }

    /// <summary>The key set for <paramref name="storeId"/> (its own, else the default one), or null.</summary>
    public virtual async Task<string?> KeyOfStoreAsync(int storeId)
    {
        return KeyOf(await _settingService.LoadSettingAsync<SattlemeSettings>(storeId));
    }

    /// <summary>
    /// <paramref name="stored"/> decrypted with <paramref name="encryptionKey"/> (the store's when
    /// empty), or null when it is not an encrypted integration key.
    /// </summary>
    public virtual string? Unprotect(string? stored, string encryptionKey)
    {
        if (string.IsNullOrEmpty(stored))
            return null;
        try
        {
            var key = _encryptionService.DecryptText(stored, encryptionKey);
            return key.StartsWith(KEY_PREFIX, StringComparison.Ordinal) ? key : null;
        }
        catch (Exception exception) when (exception is FormatException or System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return null;
        }
    }

    #endregion
}