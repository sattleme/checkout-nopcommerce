using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Payments.Sattleme.Models;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Web.Framework.Components;

namespace Nop.Plugin.Payments.Sattleme.Components;

/// <summary>
/// The checkout's payment information step: the method's description (the admin's, in the
/// customer's language, else how a Bitcoin payment goes), then what comes next, as plain text.
/// </summary>
public partial class PaymentInfoViewComponent : NopViewComponent
{
    #region Fields

    private readonly ILocalizationService _localizationService;
    private readonly ISettingService _settingService;
    private readonly IStoreContext _storeContext;
    private readonly IWorkContext _workContext;

    #endregion

    #region Ctor

    /// <summary>The component.</summary>
    public PaymentInfoViewComponent(ILocalizationService localizationService,
        ISettingService settingService,
        IStoreContext storeContext,
        IWorkContext workContext)
    {
        _localizationService = localizationService;
        _settingService = settingService;
        _storeContext = storeContext;
        _workContext = workContext;
    }

    #endregion

    #region Methods

    /// <summary>The step's text.</summary>
    public async Task<IViewComponentResult> InvokeAsync()
    {
        var store = await _storeContext.GetCurrentStoreAsync();
        var language = await _workContext.GetWorkingLanguageAsync();
        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(store.Id);
        var description = await _localizationService.GetLocalizedSettingAsync(settings, setting => setting.DescriptionText, language.Id, store.Id);
        if (string.IsNullOrWhiteSpace(description))
            description = await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + "PaymentMethodDescription");
        var minutes = Math.Clamp(settings.MinutesToPay, SattlemeDefaults.MIN_MINUTES, SattlemeDefaults.MAX_MINUTES);
        var next = string.Format(CultureInfo.CurrentCulture,
            await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + NextResource(minutes)), minutes);

        var model = new PaymentInfoModel();
        foreach (var paragraph in Paragraphs(description).Append(next))
            model.Paragraphs.Add(paragraph);
        return await ViewAsync(SattlemeDefaults.VIEWS + "PaymentInfo.cshtml", model);
    }

    #endregion

    #region Utilities

    /// <summary>
    /// The text of what comes next for <paramref name="minutes"/> minutes to pay: one minute in the
    /// singular, more in the plural (nopCommerce's resources have no plural forms: two resources).
    /// </summary>
    public static string NextResource(int minutes)
    {
        return minutes == 1 ? "PaymentInfo.NextOneMinute" : "PaymentInfo.Next";
    }

    /// <summary>The paragraphs of a plain text: its lines, without the empty ones.</summary>
    public static IEnumerable<string> Paragraphs(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0);
    }

    #endregion
}