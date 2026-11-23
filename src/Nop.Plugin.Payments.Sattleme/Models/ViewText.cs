using System.Globalization;
using System.Text.Encodings.Web;
using Nop.Web.Framework.Localization;

namespace Nop.Plugin.Payments.Sattleme.Models;

/// <summary>
/// The plugin's texts in its views, as plain text: Razor encodes them, and what fills them in. (A
/// resource from nopCommerce's T() is written as HTML, its arguments too: never so here.)
/// </summary>
public static class ViewText
{
    /// <summary>The resource <paramref name="format"/> filled in with <paramref name="arguments"/>, as plain text.</summary>
    public static string Format(LocalizedString format, params object?[] arguments)
    {
        ArgumentNullException.ThrowIfNull(format);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format.Text, arguments);
        }
        catch (FormatException)
        {
            return format.Text;
        }
    }

    /// <summary>
    /// The resource <paramref name="text"/>, HTML-encoded: for a tag helper that writes its
    /// attribute as HTML (nop-card's title).
    /// </summary>
    public static string Encoded(LocalizedString text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HtmlEncoder.Default.Encode(text.Text);
    }
}