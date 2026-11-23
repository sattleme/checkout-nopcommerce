using System.Text.Json.Nodes;
using Moq;
using Nop.Core.Domain.Configuration;
using Nop.Core.Domain.Gdpr;
using Nop.Core.Domain.Security;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Configuration;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The customer's personal data removed (nopCommerce's GDPR deletion): what leads to their coins
/// goes from their invoices, once the checks no longer need it. And the integration key, encrypted
/// again when the store's encryption key changes.
/// </summary>
[TestFixture]
public sealed class PrivacyTests
{
    private Shop _shop = null!;

    [SetUp]
    public void SetUp()
    {
        _shop = new Shop();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private static EventConsumers Consumers(Scope scope, Mock<ISettingService>? settings = null)
    {
        var resources = new Mock<PluginResources>(scope.LanguageService.Object, scope.Localization.Object, null!);
        return new EventConsumers(scope.Encryption.Object, scope.Keys, (settings ?? scope.SettingService).Object, scope.Shredder, resources.Object);
    }

    [Test]
    public async Task Removes_the_address_and_transactions_of_the_customer_s_final_invoices()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidOnchain(24_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Does.Contain(SdkData.ADDRESS));

        var scope = _shop.NewScope();
        await Consumers(scope).HandleEventAsync(new CustomerPermanentlyDeleted(_shop.Customer.Id, "customer@example.com"));

        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Shredded, Is.True);
        Assert.That(stored.InvoiceJson, Does.Not.Contain(SdkData.ADDRESS));
        Assert.That(stored.StatusJson, Does.Not.Contain(SdkData.TXID));
        var json = JsonNode.Parse(stored.InvoiceJson!)!;
        Assert.That((string?)json["onchainAddress"], Is.EqualTo(PersonalDataShredder.SHREDDED));
        Assert.That((string?)json["paymentUri"], Is.EqualTo(PersonalDataShredder.SHREDDED));
        Assert.That((string?)json["ref"], Is.EqualTo(invoice.Reference), "what is not personal stays");
        Assert.That((string?)json["sats"], Is.EqualTo("24500"));
        var admin = await scope.Factory.PrepareAdminOrderPaymentAsync(_shop.Order(order.Id), _shop.Database.Invoices());
        Assert.That(admin.Invoices.Single().Shredded, Is.True);
        Assert.That(admin.Invoices.Single().Txids, Is.Empty);
        Assert.That(admin.Invoices.Single().AddressLink, Is.False);
    }

    [Test]
    public async Task Keeps_what_the_checks_need_until_the_invoice_is_final()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 1));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        await Consumers(_shop.NewScope()).HandleEventAsync(new CustomerPermanentlyDeleted(_shop.Customer.Id, "customer@example.com"));
        var pending = _shop.Database.Invoices().Single();
        Assert.That(pending.Shredded, Is.True);
        Assert.That(pending.InvoiceJson, Does.Contain(SdkData.ADDRESS), "its check still needs it");

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidOnchain(24_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        var final = _shop.Database.Invoices().Single();
        Assert.That(final.Final, Is.True);
        Assert.That(final.InvoiceJson, Does.Not.Contain(SdkData.ADDRESS));
        Assert.That(final.StatusJson, Does.Not.Contain(SdkData.TXID));
        Assert.That(_shop.Order(order.Id).PaymentStatus, Is.EqualTo(Core.Domain.Payments.PaymentStatus.Paid));
    }

    [Test]
    public async Task Leaves_other_customers_invoices_alone()
    {
        var order = _shop.AddOrder();
        var invoice = (await _shop.NewScope().Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidOnchain(24_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        await Consumers(_shop.NewScope()).HandleEventAsync(new CustomerPermanentlyDeleted(_shop.Customer.Id + 1, "other@example.com"));

        Assert.That(_shop.Database.Invoices().Single().Shredded, Is.False);
        Assert.That(_shop.Database.Invoices().Single().InvoiceJson, Does.Contain(SdkData.ADDRESS));
    }

    [Test]
    public void Removes_the_rest_s_QR_code_which_holds_the_address()
    {
        var status = SdkData.OpenWithRest(1, 20_000, 4_500).ToJson();
        var shredded = JsonNode.Parse(PersonalDataShredder.ShredStatus(status)!)!;
        Assert.That(shredded["rest"], Is.Null);
        Assert.That((string?)shredded["received"], Is.EqualTo("20000"));
        Assert.That(PersonalDataShredder.ShredStatus(null), Is.Null);
        var lightning = SdkData.InvoiceJson("nop-1", "1.00", "CHF", 1, 2, "n", onchain: false).ToJsonString();
        Assert.That(PersonalDataShredder.ShredInvoice(lightning), Does.Contain("lightning:"), "a Lightning invoice names the merchant's node only");
    }

    [Test]
    public async Task Encrypts_the_saved_keys_again_when_the_store_s_encryption_key_changes()
    {
        var scope = _shop.NewScope();
        var name = "SattlemeSettings.EncryptedIntegrationKey";
        var settings = new Mock<ISettingService>();
        settings.Setup(s => s.GetAllSettingsAsync()).ReturnsAsync([
            new Setting { Name = name, Value = Encryption.Encrypt(SdkData.KEY, "old-key"), StoreId = 0 },
            new Setting { Name = name.ToLowerInvariant(), Value = Encryption.Encrypt(SdkData.KEY, "old-key"), StoreId = 2 },
            new Setting { Name = name, Value = "", StoreId = 3 },
            new Setting { Name = "SattlemeSettings.MinutesToPay", Value = "15", StoreId = 0 },
        ]);
        var saved = new List<(string Name, string Value, int Store)>();
        settings.Setup(s => s.SetSettingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((string key, string value, int store, bool _) => saved.Add((key, value, store)))
            .Returns(Task.CompletedTask);

        await Consumers(scope, settings).HandleEventAsync(new SecuritySettingsChangedEvent(new SecuritySettings { EncryptionKey = "new-key" }, "old-key"));

        Assert.That(saved.Select(entry => entry.Store), Is.EquivalentTo(new[] { 0, 2 }));
        Assert.That(saved.Select(entry => Encryption.Decrypt(entry.Value, "new-key")), Has.All.EqualTo(SdkData.KEY));
        settings.Verify(s => s.ClearCacheAsync(), Times.Once);
    }
}