using Nop.Core.Domain.Localization;
using Nop.Core.Infrastructure;
using Nop.Services.Localization;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The plugin's texts: English source strings, and their French, German and Italian translations,
/// each a nopCommerce language pack (Localization/en.xml, fr.xml, de.xml, it.xml, the format of
/// Admin → Languages → Import resources). Each language of the store gets the pack of its language
/// (by its two-letter code), English otherwise; a language added later gets its own when it is
/// created. The payment method is called "Bitcoin" in every language until the admin names it
/// otherwise.
/// </summary>
public partial class PluginResources
{
    #region Fields

    /// <summary>The languages the plugin is translated into, English first (the source).</summary>
    public static readonly IReadOnlyList<string> Languages = ["en", "fr", "de", "it"];

    /// <summary>The name of the payment method, until the admin gives it another one.</summary>
    public const string DEFAULT_TITLE = "Bitcoin";

    private readonly ILanguageService _languageService;
    private readonly ILocalizationService _localizationService;
    private readonly INopFileProvider _fileProvider;

    #endregion

    #region Ctor

    /// <summary>The texts, read from the plugin's directory through <paramref name="fileProvider"/>.</summary>
    public PluginResources(ILanguageService languageService, ILocalizationService localizationService, INopFileProvider fileProvider)
    {
        _languageService = languageService;
        _localizationService = localizationService;
        _fileProvider = fileProvider;
    }

    #endregion

    #region Methods

    /// <summary>Installs the texts in every language of the store.</summary>
    public virtual async Task InstallAsync()
    {
        foreach (var language in await _languageService.GetAllLanguagesAsync(showHidden: true))
            await InstallAsync(language);
    }

    /// <summary>Installs the texts in <paramref name="language"/>: its pack, or English's.</summary>
    public virtual async Task InstallAsync(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        using (var reader = new StreamReader(PackPath(LanguageOf(language)), System.Text.Encoding.UTF8))
            await _localizationService.ImportResourcesFromXmlAsync(language, reader, updateExistingResources: true);

        // The method's name at checkout: "Bitcoin", unless the admin named it.
        var title = await _localizationService.GetResourceAsync(TitleResource, language.Id, logIfNotFound: false, defaultValue: "", returnEmptyIfNotFound: true);
        if (string.IsNullOrEmpty(title))
            await _localizationService.AddOrUpdateLocaleResourceAsync(new Dictionary<string, string> { [TitleResource] = DEFAULT_TITLE }, language.Id);
    }

    /// <summary>Removes the texts, and the method's names.</summary>
    public virtual async Task UninstallAsync()
    {
        await _localizationService.DeleteLocaleResourcesAsync(SattlemeDefaults.RESOURCE_PREFIX);
        await _localizationService.DeleteLocaleResourcesAsync(TitleResource);
    }

    /// <summary>The pack <paramref name="language"/> (en, fr, de, it) of the plugin's directory.</summary>
    public virtual string PackPath(string language)
    {
        return _fileProvider.MapPath($"~/Plugins/{SattlemeDefaults.SYSTEM_NAME}/{SattlemeDefaults.LOCALIZATION_DIRECTORY}/{language}.xml");
    }

    /// <summary>The pack of <paramref name="language"/>: by its two-letter code (de for de-CH), English otherwise.</summary>
    public static string LanguageOf(Language language)
    {
        ArgumentNullException.ThrowIfNull(language);
        var code = (string.IsNullOrEmpty(language.UniqueSeoCode) ? language.LanguageCulture?.Split('-')[0] : language.UniqueSeoCode)?.ToLowerInvariant();
        return code is not null && Languages.Contains(code) ? code : "en";
    }

    #endregion

    #region Properties

    /// <summary>The resource of the payment method's name (nopCommerce's localized friendly name of the plugin).</summary>
    public static string TitleResource => $"{NopLocalizationDefaults.PluginNameLocaleStringResourcesPrefix}{SattlemeDefaults.SYSTEM_NAME}";

    #endregion
}