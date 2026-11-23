using System.IO.Compression;
using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Razor;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The archive's views compile as nopCommerce compiles a plugin's views: at run time, with Razor's
/// runtime compilation (Nop.Core.csproj's Microsoft.AspNetCore.Mvc.Razor.RuntimeCompilation,
/// AddRazorRuntimeCompilation in Nop.Web.Framework's ServiceCollectionExtensions.cs:322). The same
/// Razor engine and C# compiler as the shop's, with the imports of the plugin's own folders only
/// (nopCommerce's Views/_ViewImports.cshtml is not theirs) and the shop's assemblies: a view that
/// names what its imports do not bring fails here, not on the customer's page.
/// </summary>
[TestFixture]
public sealed class ViewCompilationTests
{
    private static readonly string[] _views =
        ["AdminOrder.cshtml", "Configure.cshtml", "OrderPayment.cshtml", "Pay.cshtml", "PaymentInfo.cshtml"];

    [Test]
    public void The_archive_s_views_compile_as_nopCommerce_compiles_them()
    {
        var root = Directory.CreateTempSubdirectory("sattleme-views-").FullName;
        try
        {
            using (var archive = ZipFile.OpenRead(Repository.Archive))
            {
                foreach (var entry in archive.Entries.Where(entry => entry.FullName.EndsWith(".cshtml", StringComparison.Ordinal)))
                {
                    // Where the shop has them: Plugins/Payments.Sattleme/Views/….
                    var path = Path.Combine(root, "Plugins", entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    entry.ExtractToFile(path);
                }
            }

            var (views, errors) = Compile(root);

            Assert.That(views, Is.EquivalentTo(_views.Select(view => "/Plugins/Payments.Sattleme/Views/" + view)));
            Assert.That(errors, Is.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void Finds_a_view_that_names_what_its_imports_do_not_bring()
    {
        // The plugin's imports without nopCommerce's HTML helper, which its pages use.
        var root = Directory.CreateTempSubdirectory("sattleme-views-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(root, "Plugins", "Payments.Sattleme", "Views")).FullName;
            File.WriteAllText(Path.Combine(folder, "_ViewImports.cshtml"),
                "@inherits Nop.Web.Framework.Mvc.Razor.NopRazorPage<TModel>\n@addTagHelper *, Nop.Web.Framework\n");
            File.WriteAllText(Path.Combine(folder, "Page.cshtml"), "@{\n    NopHtml.AddTitleParts(\"Bitcoin\");\n}\n<p>@T(\"Bitcoin\").Text</p>\n");

            var (views, errors) = Compile(root);

            Assert.That(views, Is.EqualTo(new[] { "/Plugins/Payments.Sattleme/Views/Page.cshtml" }));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("CS0103").And.Contain("NopHtml"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Generates each view's C# as the runtime compilation does (RazorExtensions, the tag helpers of
    /// the referenced assemblies, the view components'), then compiles them all: the views, and the
    /// errors (Razor's and C#'s).
    /// </summary>
    private static (List<string> Views, List<string> Errors) Compile(string root)
    {
        var references = References();
        var fileSystem = RazorProjectFileSystem.Create(root);
        var engine = RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem, builder =>
        {
            RazorExtensions.Register(builder);
            builder.Features.Add(new DefaultMetadataReferenceFeature { References = references });
            builder.Features.Add(new CompilationTagHelperFeature());
            builder.Features.Add(new DefaultTagHelperDescriptorProvider());
            builder.Features.Add(new ViewComponentTagHelperDescriptorProvider());
        });

        var views = new List<string>();
        var errors = new List<string>();
        var trees = new List<SyntaxTree>();
        // The language version of the shop's compiler (this one, the same package): its latest.
        var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        foreach (var item in fileSystem.EnumerateItems("/").Where(item => !item.FileName.StartsWith('_')).OrderBy(item => item.FilePath, StringComparer.Ordinal))
        {
            views.Add(item.FilePath);
            var code = engine.Process(item).GetCSharpDocument();
            errors.AddRange(code.Diagnostics.Where(diagnostic => diagnostic.Severity == RazorDiagnosticSeverity.Error)
                .Select(diagnostic => $"{item.FilePath}: {diagnostic}"));
            trees.Add(CSharpSyntaxTree.ParseText(code.GeneratedCode, options, item.FilePath));
        }

        var compilation = CSharpCompilation.Create("Views", trees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        errors.AddRange(compilation.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .Select(diagnostic => diagnostic.ToString()));
        return (views, errors);
    }

    /// <summary>The shop's assemblies: .NET's and ASP.NET Core's, nopCommerce's and their packages, the plugin's.</summary>
    private static List<MetadataReference> References()
    {
        var assemblies = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        return assemblies.Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToList();
    }
}