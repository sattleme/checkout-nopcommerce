using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentMigrator.Builders.Create.Table;
using FluentMigrator.Expressions;
using Nop.Core;
using Nop.Core.Infrastructure;
using Nop.Data;
using Nop.Data.Configuration;
using Nop.Plugin.Payments.Sattleme.Data;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NopSattleme.Net.Codecrete.QrCodeGenerator;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// What the pages are made of: the QR code drawn on the server from numbers alone, the views
/// encoding what they show and loading no script but the plugin's file, the tables of the
/// migration matching the records.
/// </summary>
[TestFixture]
public sealed partial class PageSafetyTests
{
    private const string URI = "bitcoin:bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e?amount=0.000245&lightning=lnbc245u1fake";

    [Test]
    public void Draws_the_QR_code_as_an_inline_SVG_of_the_payment_URI()
    {
        var svg = QrSvg.Of(URI, "QR code")!;
        var document = XDocument.Parse(svg);
        var code = QrCode.EncodeText(URI, QrCode.Ecc.Medium);
        var size = code.Size + (2 * QrSvg.QUIET_ZONE);

        Assert.That(document.Root!.Name.LocalName, Is.EqualTo("svg"));
        Assert.That((string?)document.Root.Attribute("viewBox"), Is.EqualTo($"0 0 {size} {size}"));
        Assert.That((string?)document.Root.Attribute("role"), Is.EqualTo("img"));
        // Every dark module, and nothing else, is covered by the path's runs.
        var path = (string)document.Root.Elements().Single(element => element.Name.LocalName == "path").Attribute("d")!;
        var dark = new HashSet<(int, int)>();
        foreach (Match run in Runs().Matches(path))
        {
            var (x, y, width) = (int.Parse(run.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(run.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(run.Groups[3].Value, CultureInfo.InvariantCulture));
            for (var i = 0; i < width; i++)
                Assert.That(dark.Add((x + i - QrSvg.QUIET_ZONE, y - QrSvg.QUIET_ZONE)), Is.True);
        }
        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
                Assert.That(dark.Contains((x, y)), Is.EqualTo(code.GetModule(x, y)), $"({x}, {y})");
        }
    }

    [GeneratedRegex(@"M(\d+) (\d+)h(\d+)v1h-\3z")]
    private static partial Regex Runs();

    [Test]
    public void The_QR_code_holds_nothing_but_numbers_and_its_label_encoded()
    {
        var svg = QrSvg.Of(URI, "<script>alert(\"x\")</script> & co")!;

        Assert.That(svg, Does.Not.Contain("<script"));
        Assert.That(svg, Does.Contain("aria-label=\"&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; co\""));
        Assert.That(svg, Does.Not.Contain(URI), "the URI is drawn, never written");
        Assert.That(Regex.Replace(svg, "aria-label=\"[^\"]*\"", ""), Does.Match("^[<>a-zA-Z0-9 =\"/:.#\\-]*$"));
    }

    [Test]
    public void A_text_too_long_for_a_QR_code_gets_none()
    {
        Assert.That(QrSvg.Of(new string('a', 5000), "QR code"), Is.Null);
    }

    private static string View(string name)
    {
        return File.ReadAllText(Path.Combine(Repository.Plugin, "Views", name));
    }

    [Test]
    public void The_payment_page_loads_its_script_from_the_plugin_and_has_none_inline()
    {
        var page = View("Pay.cshtml");

        Assert.That(page, Does.Not.Contain("<script"));
        Assert.That(page, Does.Contain("NopHtml.AddScriptParts(ResourceLocation.Footer, SattlemeDefaults.CONTENT + \"pay.js\""));
        Assert.That(page, Does.Contain("<meta name=\\\"robots\\\" content=\\\"noindex, nofollow\\\" />"));
        Assert.That(page, Does.Contain("<noscript>").And.Contain("Model.CheckUrl"), "a button to check again without JavaScript");
        Assert.That(page, Does.Contain("@Html.AntiForgeryToken()"));
        Assert.That(SattlemeDefaults.CONTENT, Does.StartWith("~/Plugins/"), "served by the shop: no other host");
        var script = File.ReadAllText(Path.Combine(Repository.Plugin, "Content", "pay.js"));
        Assert.That(script, Does.Not.Contain("http://").And.Not.Contain("https://").And.Not.Contain("innerHTML").And.Not.Contain("eval("));
    }

    [Test]
    public void The_views_encode_what_they_show()
    {
        foreach (var file in Directory.GetFiles(Path.Combine(Repository.Plugin, "Views"), "*.cshtml"))
        {
            var view = File.ReadAllText(file);
            var raw = Regex.Matches(view, @"Html\.Raw\(([^)]*)\)").Select(match => match.Groups[1].Value).ToList();
            Assert.That(raw, Is.Empty.Or.EqualTo(new[] { "Model.QrSvg" }), file);
            // nopCommerce's T() writes a resource as HTML: the plugin's views write its text, encoded.
            Assert.That(Regex.Matches(view, @"@T\(""[^""]*""\)(?!\.Text)").Select(match => match.Value), Is.Empty, file);
        }
    }

    [Test]
    public void The_migration_s_tables_hold_every_field_of_the_records()
    {
        Singleton<DataConfig>.Instance = new DataConfig { DataProvider = DataProviderType.PostgreSQL };
        AssertMapped<SattlemeOrder>(new SattlemeOrderBuilder());
        AssertMapped<SattlemeInvoice>(new SattlemeInvoiceBuilder());
    }

    private static void AssertMapped<TEntity>(Nop.Data.Mapping.Builders.NopEntityBuilder<TEntity> builder)
        where TEntity : BaseEntity
    {
        var expression = new CreateTableExpression { TableName = typeof(TEntity).Name };
        var table = new CreateTableExpressionBuilder(expression, new Nop.Data.Migrations.NullMigrationContext());
        builder.MapEntity(table);
        var columns = expression.Columns.Select(column => column.Name).ToList();
        var fields = typeof(TEntity).GetProperties().Select(property => property.Name).Where(name => name != nameof(BaseEntity.Id));
        Assert.That(columns, Is.EquivalentTo(fields), typeof(TEntity).Name);
    }
}