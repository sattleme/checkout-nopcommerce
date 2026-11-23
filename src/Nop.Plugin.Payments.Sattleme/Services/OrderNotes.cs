using System.Globalization;
using Nop.Core.Domain.Localization;
using Nop.Core.Domain.Orders;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Services.Localization;
using Nop.Services.Orders;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The notes the plugin writes in an order's history (the admin area's order page), in the admin
/// area's default language. They never hold an address or a transaction (those lead to the
/// customer's coins: the order's page shows them from the invoice, which the GDPR deletion empties).
/// </summary>
public partial class OrderNotes
{
    #region Fields

    private readonly ILanguageService _languageService;
    private readonly ILocalizationService _localizationService;
    private readonly IOrderService _orderService;
    private readonly LocalizationSettings _localizationSettings;
    private readonly TimeProvider _time;

    #endregion

    #region Ctor

    /// <summary>The notes, written through <paramref name="orderService"/>.</summary>
    public OrderNotes(ILanguageService languageService,
        ILocalizationService localizationService,
        IOrderService orderService,
        LocalizationSettings localizationSettings,
        TimeProvider time)
    {
        _languageService = languageService;
        _localizationService = localizationService;
        _orderService = orderService;
        _localizationSettings = localizationSettings;
        _time = time;
    }

    #endregion

    #region Methods

    /// <summary>
    /// Adds the note <paramref name="resource"/> (a resource of the plugin, a composite format) to
    /// the order <paramref name="orderId"/>, with <paramref name="arguments"/> (numbers of sats
    /// written as the admin language writes numbers, a <see cref="NoteResource"/> as its text in
    /// that language).
    /// </summary>
    public virtual async Task AddAsync(int orderId, string resource, params object[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var languageId = _localizationSettings.DefaultAdminLanguageId;
        var language = await _languageService.GetLanguageByIdAsync(languageId);
        var culture = CultureOf(language?.LanguageCulture);
        var format = await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + resource, languageId);
        var values = new object[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            values[i] = arguments[i] switch
            {
                long sats => sats.ToString("N0", culture),
                NoteResource resourceArgument => await TextAsync(resourceArgument.Name, languageId),
                var other => other,
            };
        }
        string text;
        try
        {
            text = string.Format(culture, format, values);
        }
        catch (FormatException)
        {
            text = format + " " + string.Join(" ", values);
        }

        await _orderService.InsertOrderNoteAsync(new OrderNote
        {
            OrderId = orderId,
            Note = text,
            DisplayToCustomer = false,
            CreatedOnUtc = _time.GetUtcNow().UtcDateTime,
        });
    }

    /// <summary>
    /// Notes the invoice <paramref name="invoice"/> made for the order <paramref name="orderId"/>:
    /// its reference, its amount in sats, its end (in UTC: a note is text, the same for every
    /// reader), and how it can be paid (in Lightning, on-chain, or both). Never its address.
    /// Nothing when it was not made.
    /// </summary>
    public virtual async Task AddMadeAsync(int orderId, SattlemeInvoice invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        if (StoredInvoice.Of(invoice) is not { } made)
            return;
        var end = DateTimeOffset.FromUnixTimeSeconds(made.ExpiresAt).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var resource = made.OnchainAddress is null ? "Note.CreatedLightning"
            : made.Bolt11 is null ? "Note.CreatedOnchain"
            : "Note.Created";
        await AddAsync(orderId, resource, invoice.Reference, made.Sats, end);
    }

    #endregion

    #region Utilities

    /// <summary>The plugin's resource <paramref name="name"/> in the language <paramref name="languageId"/>; the name itself when it has none.</summary>
    private async Task<string> TextAsync(string name, int languageId)
    {
        var text = await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + name, languageId,
            logIfNotFound: false, defaultValue: "", returnEmptyIfNotFound: true);
        return string.IsNullOrEmpty(text) ? name : text;
    }

    private static CultureInfo CultureOf(string? name)
    {
        try
        {
            return string.IsNullOrEmpty(name) ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture;
        }
    }

    #endregion
}

/// <summary>An argument of a note: the plugin's resource <paramref name="Name"/>, in the note's language.</summary>
/// <param name="Name">The resource's name, after the plugin's prefix.</param>
public sealed record NoteResource(string Name);