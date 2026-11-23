using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using NUnit.Framework;
using Sattleme.Nop.Pins;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The plugin as it ships: its archive holds exactly its files, its assemblies are of its own names
/// with every embedded type under the prefix (never Sattleme.Checkout.dll nor NBitcoin.Secp256k1.dll:
/// another plugin may load other versions of those into the same host), and the pinned copies it is
/// built from are unchanged.
/// </summary>
[TestFixture]
public sealed class PackageTests
{
    /// <summary>The assemblies of the archive: the plugin's, and the SDK, NBitcoin.Secp256k1 and the QR code generator under its names.</summary>
    private static readonly string[] _assemblies =
    [
        "Nop.Plugin.Payments.Sattleme.dll",
        "Nop.Plugin.Payments.Sattleme.Sdk.dll",
        "Nop.Plugin.Payments.Sattleme.Secp256k1.dll",
        "Nop.Plugin.Payments.Sattleme.QrCode.dll",
    ];

    private static readonly string[] _embedded = ["Nop.Plugin.Payments.Sattleme.Sdk", "Nop.Plugin.Payments.Sattleme.Secp256k1", "Nop.Plugin.Payments.Sattleme.QrCode"];

    /// <summary>Names no assembly of the plugin may have, nor reference: the original packages', and QRCoder (another plugin of nopCommerce ships it).</summary>
    private static readonly string[] _foreign = ["Sattleme.Checkout", "NBitcoin.Secp256k1", "NBitcoin", "Net.Codecrete.QrCodeGenerator", "QRCoder"];

    [Test]
    public void The_pinned_copies_are_unchanged()
    {
        foreach (var pin in Pins.All)
            Assert.That(Pins.Check(Repository.Root, pin), Is.Empty, pin.Name);
    }

    [Test]
    public void The_SDK_is_pinned_from_its_repository_at_a_commit()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Repository.File("sdk", "PIN.json")));
        var pin = document.RootElement;
        Assert.That(pin.GetProperty("repository").GetString(), Is.EqualTo("https://github.com/sattleme/checkout-dotnet"));
        Assert.That(pin.GetProperty("commit").GetString(), Does.Match("^[0-9a-f]{40}$"));
        Assert.That(pin.GetProperty("version").GetString(), Is.EqualTo("1.0.0"));
        Assert.That(pin.GetProperty("files").EnumerateObject().Count(), Is.GreaterThan(40));
    }

    [Test]
    public void The_archive_holds_one_folder_with_the_plugin_s_files_only()
    {
        Assert.That(File.Exists(Repository.Archive), Is.True, Repository.Archive);
        using var archive = ZipFile.OpenRead(Repository.Archive);
        var entries = archive.Entries.Where(entry => entry.Name.Length > 0).Select(entry => entry.FullName.Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();

        var expected = _assemblies.Select(name => "Payments.Sattleme/" + name)
            .Concat(["Payments.Sattleme/plugin.json", "Payments.Sattleme/logo.png"])
            .Concat(Directory.GetFiles(Path.Combine(Repository.Plugin, "Views"), "*.cshtml").Select(file => "Payments.Sattleme/Views/" + Path.GetFileName(file)))
            .Concat(["Payments.Sattleme/Content/pay.js", "Payments.Sattleme/Content/pay.css"])
            .Concat(new[] { "en", "fr", "de", "it" }.Select(language => $"Payments.Sattleme/Localization/{language}.xml"))
            .Concat(new[] { "LICENSE", "Sattleme.Checkout.LICENSE", "NBitcoin.Secp256k1.LICENSE", "QrCodeGenerator.LICENSE" }.Select(name => "Payments.Sattleme/licenses/" + name))
            .Order(StringComparer.Ordinal).ToList();
        Assert.That(entries, Is.EqualTo(expected));
        Assert.That(entries.Where(entry => entry.EndsWith(".dll", StringComparison.Ordinal)).Select(Path.GetFileName),
            Has.None.EqualTo("Sattleme.Checkout.dll").And.None.EqualTo("NBitcoin.Secp256k1.dll"));
    }

    [Test]
    public void The_archive_s_files_are_those_the_tests_run()
    {
        using var archive = ZipFile.OpenRead(Repository.Archive);
        foreach (var name in _assemblies)
        {
            var entry = archive.GetEntry("Payments.Sattleme/" + name)!;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var loaded = Loaded().Single(assembly => assembly.GetName().Name + ".dll" == name);
            Assert.That(Convert.ToHexString(SHA256.HashData(buffer.ToArray())), Is.EqualTo(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(loaded.Location)))), name);
        }
    }

    private static IEnumerable<System.Reflection.Assembly> Loaded()
    {
        yield return typeof(SattlemePaymentMethod).Assembly;
        yield return typeof(NopSattleme.Sattleme.Checkout.SattlemeClient).Assembly;
        yield return typeof(NopSattleme.NBitcoin.Secp256k1.ECPrivKey).Assembly;
        yield return typeof(NopSattleme.Net.Codecrete.QrCodeGenerator.QrCode).Assembly;
    }

    [Test]
    public void The_archive_s_plugin_json_describes_version_1_0_0_for_nopCommerce_4_90()
    {
        using var archive = ZipFile.OpenRead(Repository.Archive);
        using var stream = archive.GetEntry("Payments.Sattleme/plugin.json")!.Open();
        using var document = JsonDocument.Parse(stream);
        var plugin = document.RootElement;
        Assert.That(plugin.GetProperty("SystemName").GetString(), Is.EqualTo(SattlemeDefaults.SYSTEM_NAME));
        Assert.That(plugin.GetProperty("Version").GetString(), Is.EqualTo("1.0.0"));
        Assert.That(plugin.GetProperty("SupportedVersions").EnumerateArray().Select(version => version.GetString()), Is.EqualTo(new[] { "5.00" }));
        Assert.That(plugin.GetProperty("FileName").GetString(), Is.EqualTo("Nop.Plugin.Payments.Sattleme.dll"));
        Assert.That(plugin.GetProperty("Group").GetString(), Is.EqualTo("Payment methods"));
        var authors = System.Xml.Linq.XDocument.Load(Repository.File("Directory.Build.props")).Descendants("Authors").Single().Value;
        Assert.That(plugin.GetProperty("Author").GetString(), Is.EqualTo(authors), "the author metadata: the name alone");
        Assert.That(Repository.Version, Is.EqualTo("1.0.0"));
    }

    private static IEnumerable<(string Name, MetadataReader Reader, PEReader Pe)> Assemblies()
    {
        foreach (var name in _assemblies)
        {
            var pe = new PEReader(File.OpenRead(Path.Combine(Repository.PackageFolder, name)));
            yield return (name, pe.GetMetadataReader(), pe);
        }
    }

    [Test]
    public void No_assembly_has_or_references_the_names_of_the_original_packages()
    {
        foreach (var (name, reader, pe) in Assemblies())
        {
            using (pe)
            {
                var own = reader.GetString(reader.GetAssemblyDefinition().Name);
                Assert.That(own + ".dll", Is.EqualTo(name));
                var references = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).ToList();
                Assert.That(references.Intersect(_foreign), Is.Empty, name);
                Assert.That(references.Where(reference => reference.StartsWith("Nop.Plugin.", StringComparison.Ordinal)),
                    Is.SubsetOf(_embedded), name);
            }
        }
    }

    [Test]
    public void Every_type_of_the_embedded_assemblies_is_under_the_prefix()
    {
        foreach (var (name, reader, pe) in Assemblies())
        {
            using (pe)
            {
                if (name == "Nop.Plugin.Payments.Sattleme.dll")
                    continue;
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    var space = reader.GetString(type.Namespace);
                    var typeName = reader.GetString(type.Name);
                    if (type.GetDeclaringType().IsNil == false || typeName == "<Module>" || typeName.StartsWith('<'))
                        continue;
                    var isPublic = (type.Attributes & System.Reflection.TypeAttributes.VisibilityMask) == System.Reflection.TypeAttributes.Public;
                    // The compiler's own attributes (Nullable…, Embedded): internal, one per assembly.
                    if (!isPublic && space is "Microsoft.CodeAnalysis" or "System.Runtime.CompilerServices" or "System.Diagnostics.CodeAnalysis")
                        continue;
                    Assert.That(space, Does.StartWith("NopSattleme."), $"{name}: {space}.{typeName}");
                }
            }
        }
    }

    [Test]
    public void The_plugin_reaches_the_SDK_through_the_prefixed_names_only()
    {
        var plugin = typeof(SattlemePaymentMethod).Assembly;
        var referenced = plugin.GetReferencedAssemblies().Select(reference => reference.Name).ToList();
        Assert.That(referenced, Does.Contain("Nop.Plugin.Payments.Sattleme.Sdk").And.Contain("Nop.Plugin.Payments.Sattleme.QrCode"));
        Assert.That(referenced.Intersect(_foreign), Is.Empty);
        Assert.That(typeof(NopSattleme.Sattleme.Checkout.SattlemeClient).Assembly.GetName().Name, Is.EqualTo("Nop.Plugin.Payments.Sattleme.Sdk"));
    }
}