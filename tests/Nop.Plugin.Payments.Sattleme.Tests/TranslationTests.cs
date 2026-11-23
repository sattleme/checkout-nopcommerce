using System.Text.RegularExpressions;
using System.Xml;
using Nop.Core.Domain.Localization;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Localization;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The plugin's texts: English, French, German and Italian, the same resources and placeholders in
/// each, every resource the code and the views use present, read as nopCommerce imports a pack.
/// </summary>
[TestFixture]
public sealed partial class TranslationTests
{
    private static readonly string[] _languages = ["en", "fr", "de", "it"];

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();

    /// <summary>The resources of a pack, as nopCommerce's LocalizationService.LoadLocaleResourcesFromStream reads them.</summary>
    private static Dictionary<string, string> Read(string language)
    {
        var result = new Dictionary<string, string>();
        using var stream = new StreamReader(Path.Combine(Repository.Plugin, "Localization", language + ".xml"));
        using var xml = XmlReader.Create(stream);
        while (xml.ReadToFollowing("Language"))
        {
            using var languageReader = xml.ReadSubtree();
            while (languageReader.ReadToFollowing("LocaleResource"))
            {
                if (xml.NodeType == XmlNodeType.Element && xml.GetAttribute("Name") is string name)
                {
                    using var resource = languageReader.ReadSubtree();
                    if (resource.ReadToFollowing("Value") && resource.NodeType == XmlNodeType.Element)
                        result.Add(name.ToLowerInvariant(), resource.ReadString());
                }
            }
            break;
        }
        return result;
    }

    [Test]
    public void Every_language_has_every_resource()
    {
        var english = Read("en");
        Assert.That(english, Has.Count.GreaterThan(100));
        foreach (var language in _languages)
        {
            var pack = Read(language);
            Assert.That(pack.Keys, Is.EquivalentTo(english.Keys), language);
            Assert.That(pack.Values, Has.All.Not.Empty, language);
            Assert.That(pack.Keys, Has.All.StartWith(SattlemeDefaults.RESOURCE_PREFIX.ToLowerInvariant()), language);
        }
    }

    [Test]
    public void Every_translation_has_the_placeholders_of_the_English_text()
    {
        var english = Read("en");
        foreach (var language in _languages.Skip(1))
        {
            var pack = Read(language);
            foreach (var (name, value) in english)
            {
                var expected = Placeholder().Matches(value).Select(match => match.Groups[1].Value).Order().ToList();
                var actual = Placeholder().Matches(pack[name]).Select(match => match.Groups[1].Value).Order().ToList();
                Assert.That(actual, Is.EqualTo(expected), $"{language}: {name}");
                Assert.DoesNotThrow(() => string.Format(System.Globalization.CultureInfo.InvariantCulture, pack[name], [.. Enumerable.Range(0, 5).Select(i => (object)i)]), $"{language}: {name}");
            }
        }
    }

    [TestCase("en", "minute", "minutes")]
    [TestCase("fr", "minute", "minutes")]
    [TestCase("de", "Minute", "Minuten")]
    [TestCase("it", "minuto", "minuti")]
    public void Says_one_minute_in_the_singular_and_more_in_the_plural(string language, string one, string more)
    {
        var pack = Read(language);
        string Next(int minutes)
        {
            var name = (SattlemeDefaults.RESOURCE_PREFIX + Components.PaymentInfoViewComponent.NextResource(minutes)).ToLowerInvariant();
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, pack[name], minutes);
        }

        Assert.That(Next(1), Does.EndWith($" 1 {one}.").Or.Contain($" 1 {one} "), language);
        Assert.That(Next(1), Does.Not.Contain(more), language);
        foreach (var minutes in new[] { 2, 15, 60 })
            Assert.That(Next(minutes), Does.Contain($" {minutes} {more}"), language);
    }

    [Test]
    public void German_is_spelled_as_in_Switzerland()
    {
        Assert.That(Read("de").Values, Has.None.Contains("ß"));
    }

    /// <summary>The resources the plugin's code and views name: literally, or built from a known list.</summary>
    private static IEnumerable<string> Used()
    {
        var files = Directory.GetFiles(Repository.Plugin, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Combine(Repository.Plugin, "Views"), "*.cshtml"))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var literal = new Regex("\"Plugins\\.Payments\\.Sattleme\\.([A-Za-z.]+[A-Za-z])\"");
        var relative = new Regex("(?:RESOURCE_PREFIX \\+ |(?<=[ (])ResourceAsync\\(|Refused\\(|AddAsync\\([^,]+, |NoteResource\\(|\\? |: |=> )\"((?:[A-Z][A-Za-z]*\\.)*[A-Z][A-Za-z]*(?:\\.[A-Za-z]+)?)\"");
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match match in literal.Matches(text))
                yield return match.Groups[1].Value;
            foreach (Match match in relative.Matches(text))
            {
                var name = match.Groups[1].Value;
                if (!name.StartsWith("Plugins.", StringComparison.Ordinal) && (name.Contains('.', StringComparison.Ordinal) || name == "PaymentMethodDescription"))
                    yield return name;
            }
        }
        foreach (var phase in Enum.GetNames<PaymentPhase>())
            yield return "Order.Status." + phase.ToLowerInvariant();
        foreach (var state in new[] { "none", "unchecked", "open", "unconfirmed", "paid", "expired", "abandoned" })
            yield return "State." + state;
        foreach (var code in ErrorCodes())
            yield return "ErrorCode." + code;
        foreach (var field in new[] { "Enabled", "IntegrationKey", "RemoveKey", "MinutesToPay", "Title", "DescriptionText", "AdditionalFee", "AdditionalFeePercentage" })
        {
            yield return "Fields." + field;
            yield return "Fields." + field + ".Hint";
        }
    }

    /// <summary>Every error code a record can hold: the SDK's (its exceptions' codes) and the plugin's own.</summary>
    private static IEnumerable<string> ErrorCodes()
    {
        var sdk = typeof(NopSattleme.Sattleme.Checkout.SattlemeException).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(NopSattleme.Sattleme.Checkout.SattlemeException)) && type.IsPublic)
            .Select(type => type.Name);
        Assert.That(sdk, Is.SupersetOf(new[] { "NetworkException", "RevokedException", "InvalidKeyException" }));
        return ["network", "timeout", "invalid_key", "invalid_option", "invalid_order", "revoked", "channel_not_found", "invalid_invoice",
            "unsupported_currency", "rate", "checkout_unavailable", "onchain_unavailable", "mismatch", "apply", "error", "no_key", "busy", "currency"];
    }

    [Test]
    public void Every_resource_the_plugin_uses_is_translated()
    {
        var english = Read("en");
        var used = Used().Distinct().ToList();
        Assert.That(used, Has.Count.GreaterThan(100));
        foreach (var name in used)
            Assert.That(english.ContainsKey((SattlemeDefaults.RESOURCE_PREFIX + name).ToLowerInvariant()), Is.True, name);
    }

    [Test]
    public void Every_resource_of_the_packs_is_used()
    {
        var used = Used().Select(name => (SattlemeDefaults.RESOURCE_PREFIX + name).ToLowerInvariant()).ToHashSet();
        Assert.That(Read("en").Keys.Where(name => !used.Contains(name)), Is.Empty);
    }

    [Test]
    public void Every_error_code_of_the_SDK_has_a_text()
    {
        var english = Read("en");
        var codes = new[]
        {
            new NopSattleme.Sattleme.Checkout.NetworkException("x").Code, new NopSattleme.Sattleme.Checkout.RevokedException().Code,
            new NopSattleme.Sattleme.Checkout.ChannelNotFoundException(false).Code, new NopSattleme.Sattleme.Checkout.InvalidKeyException().Code,
            new NopSattleme.Sattleme.Checkout.InvalidOptionException("p", "m").Code, new NopSattleme.Sattleme.Checkout.InvalidOrderException("p", "m").Code,
            new NopSattleme.Sattleme.Checkout.InvalidInvoiceException("m").Code, new NopSattleme.Sattleme.Checkout.UnsupportedCurrencyException("XXX", "s").Code,
            new NopSattleme.Sattleme.Checkout.RateException("m").Code, new NopSattleme.Sattleme.Checkout.CheckoutUnavailableException().Code,
            new NopSattleme.Sattleme.Checkout.OnchainUnavailableException().Code,
        };
        foreach (var code in codes)
            Assert.That(english.ContainsKey((SattlemeDefaults.RESOURCE_PREFIX + "ErrorCode." + code).ToLowerInvariant()), Is.True, code);
    }

    [TestCase("en", "en-US", "en")]
    [TestCase("de", "de-CH", "de")]
    [TestCase("", "fr-CH", "fr")]
    [TestCase("it", "it-CH", "it")]
    [TestCase("rm", "rm-CH", "en")]
    [TestCase("es", "es-ES", "en")]
    public void Gives_each_language_its_pack_English_otherwise(string seoCode, string culture, string pack)
    {
        Assert.That(PluginResources.LanguageOf(new Language { UniqueSeoCode = seoCode, LanguageCulture = culture }), Is.EqualTo(pack));
    }

    [Test]
    public void Names_the_method_through_nopCommerce_s_localized_plugin_name()
    {
        Assert.That(PluginResources.TitleResource, Is.EqualTo(NopLocalizationDefaults.PluginNameLocaleStringResourcesPrefix + SattlemeDefaults.SYSTEM_NAME));
        Assert.That(PluginResources.DEFAULT_TITLE, Is.EqualTo("Bitcoin"));
    }
}