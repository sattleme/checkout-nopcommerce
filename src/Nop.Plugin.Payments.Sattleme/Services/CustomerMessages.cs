using System.Globalization;
using Nop.Services.Helpers;
using Nop.Services.Localization;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>What the customer is told, in their language and time zone.</summary>
public partial class CustomerMessages
{
    #region Fields

    private readonly IDateTimeHelper _dateTimeHelper;
    private readonly ILocalizationService _localizationService;

    #endregion

    #region Ctor

    /// <summary>The messages.</summary>
    public CustomerMessages(IDateTimeHelper dateTimeHelper, ILocalizationService localizationService)
    {
        _dateTimeHelper = dateTimeHelper;
        _localizationService = localizationService;
    }

    #endregion

    #region Methods

    /// <summary>Why no invoice is made (<paramref name="made"/>'s refusal), in the working language.</summary>
    public virtual async Task<string> RefusalAsync(MakeResult made)
    {
        ArgumentNullException.ThrowIfNull(made);
        var text = await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + made.Refusal);
        if (made.RefusalArguments.Length == 0)
            return text;
        var values = new List<object>();
        foreach (var argument in made.RefusalArguments)
            values.Add(argument is DateTime utc ? await TimeAsync(utc) : argument);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, [.. values]);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary><paramref name="utc"/> as the customer reads a time: in their time zone, short.</summary>
    public virtual async Task<string> TimeAsync(DateTime utc)
    {
        var local = await _dateTimeHelper.ConvertToUserTimeAsync(utc, DateTimeKind.Utc);
        return local.ToString("t", CultureInfo.CurrentCulture);
    }

    #endregion
}