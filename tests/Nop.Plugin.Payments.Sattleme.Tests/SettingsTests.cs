using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.Sattleme.Controllers;
using Nop.Plugin.Payments.Sattleme.Models;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Configuration;
using Nop.Services.Messages;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Mvc.Filters;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The configuration page: the key verified when saved, stored encrypted, never sent back to the
/// browser; the time to pay; the method offered or not.
/// </summary>
[TestFixture]
public sealed class SettingsTests
{
    private const string NEW_KEY = "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o.uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkuZXhhbXBsZQ";
    private Shop _shop = null!;
    private Scope _scope = null!;
    private Mock<ISettingService> _settings = null!;
    private Mock<INotificationService> _notifications = null!;
    private PaymentSettings _paymentSettings = null!;
    private SattlemeSettings _saved = null!;
    private List<(string Property, bool Override)> _savedProperties = null!;
    private ISattlemeGateway _gateway = null!;

    [SetUp]
    public void SetUp()
    {
        _shop = new Shop();
        _shop.Settings[0].EncryptedIntegrationKey = "";
        _scope = _shop.NewScope();
        _settings = _scope.SettingService;
        _paymentSettings = new PaymentSettings { ActivePaymentMethodSystemNames = [] };
        _savedProperties = [];
        _settings.Setup(s => s.LoadSettingAsync<PaymentSettings>(It.IsAny<int>())).ReturnsAsync(() => _paymentSettings);
        Capture<string>();
        Capture<int>();
        Capture<decimal>();
        Capture<bool>();
        _notifications = new Mock<INotificationService>();
        _gateway = _shop.Gateway;
    }

    private void Capture<TProperty>()
    {
        _settings.Setup(s => s.SaveSettingOverridablePerStoreAsync(It.IsAny<SattlemeSettings>(), It.IsAny<Expression<Func<SattlemeSettings, TProperty>>>(), It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<bool>()))
            .Callback((SattlemeSettings settings, Expression<Func<SattlemeSettings, TProperty>> property, bool overridden, int _, bool _) =>
            {
                _saved = settings;
                _savedProperties.Add((((MemberExpression)property.Body).Member.Name, overridden));
                _shop.Settings[0] = Shop.Copy(settings);
            })
            .Returns(Task.CompletedTask);
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private SattlemeAdminController Controller()
    {
        var plugins = new Mock<IPaymentPluginManager>();
        return new SattlemeAdminController(_scope.LanguageService.Object, _scope.Localization.Object, _scope.Logger.Object, _scope.Keys, _scope.Maker,
            _notifications.Object, plugins.Object, _gateway, _settings.Object, _scope.StoreContext.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static ConfigurationModel Posted(string? key = null, int minutes = 15, bool enabled = false)
    {
        return new ConfigurationModel { IntegrationKey = key, MinutesToPay = minutes, Enabled = enabled };
    }

    private string Text(string name)
    {
        return _shop.Texts.Values[SattlemeDefaults.RESOURCE_PREFIX + name];
    }

    [Test]
    public async Task Saves_a_verified_key_encrypted_and_never_sends_it_back()
    {
        var controller = Controller();
        controller.ModelState.SetModelValue(nameof(ConfigurationModel.IntegrationKey), NEW_KEY, NEW_KEY);
        var result = await controller.Configure(Posted("  " + NEW_KEY + "  "));

        Assert.That(result, Is.InstanceOf<RedirectToActionResult>(), "shown again by a GET: nothing entered goes back");
        Assert.That(_shop.Gateway.Merchants, Is.EqualTo(1), "verified with the SDK");
        Assert.That(_saved.EncryptedIntegrationKey, Is.Not.Empty.And.Not.Contains(NEW_KEY));
        Assert.That(Encryption.Decrypt(_saved.EncryptedIntegrationKey), Is.EqualTo(NEW_KEY));
        Assert.That(_savedProperties, Does.Contain((nameof(SattlemeSettings.EncryptedIntegrationKey), true)));
        _notifications.Verify(n => n.SuccessNotification(string.Format(System.Globalization.CultureInfo.CurrentCulture, Text("Key.Verified"), "Sunrise Bakery"), It.IsAny<bool>(), It.IsAny<int>()));
        Assert.That(_shop.Log.Select(entry => entry.Message), Has.None.Contains(NEW_KEY));

        var page = (ConfigurationModel)((ViewResult)await Controller().Configure()).Model!;
        Assert.That(page.IntegrationKey, Is.Null);
        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.Saved")));
        Assert.That(JsonSerializer.Serialize(page), Does.Not.Contain(NEW_KEY).And.Not.Contain(_saved.EncryptedIntegrationKey));
    }

    private static IEnumerable<TestCaseData> Refusals()
    {
        yield return new TestCaseData("other_ck_" + new string('a', 40), null, "Key.Invalid").SetName("Refuses_what_is_not_an_integration_key");
        yield return new TestCaseData(NEW_KEY + "x", null, "Key.Invalid").SetName("Refuses_a_damaged_key");
        yield return new TestCaseData(NEW_KEY, new RevokedException(), "Key.Revoked").SetName("Refuses_a_removed_integration");
        yield return new TestCaseData(NEW_KEY, new ChannelNotFoundException(false), "Key.NotFound").SetName("Refuses_an_integration_not_found");
        yield return new TestCaseData(NEW_KEY, new ChannelNotFoundException(true), "Key.Newer").SetName("Refuses_an_integration_of_a_newer_format");
        yield return new TestCaseData(NEW_KEY, new NetworkException("relays unreachable"), "Key.Unverified").SetName("Refuses_a_key_it_could_not_verify");
        yield return new TestCaseData(NEW_KEY, new OperationCanceledException(), "Key.Unverified").SetName("Refuses_a_key_whose_verification_took_too_long");
    }

    [TestCaseSource(nameof(Refusals))]
    public async Task Refuses_a_key_with_why_in_plain_words_and_never_echoes_it(string key, Exception? error, string message)
    {
        if (error is not null)
            _shop.Gateway.Merchant = _ => throw error;
        var controller = Controller();
        controller.ModelState.SetModelValue(nameof(ConfigurationModel.IntegrationKey), key, key);
        var result = await controller.Configure(Posted(key));

        var view = (ViewResult)result;
        var model = (ConfigurationModel)view.Model!;
        Assert.That(model.IntegrationKey, Is.Null);
        var entry = controller.ModelState[nameof(ConfigurationModel.IntegrationKey)]!;
        Assert.That(entry.RawValue, Is.Null);
        Assert.That(entry.AttemptedValue, Is.Null);
        Assert.That(entry.Errors.Single().ErrorMessage, Is.EqualTo(Text(message)));
        Assert.That(JsonSerializer.Serialize(model), Does.Not.Contain(key));
        Assert.That(_savedProperties, Is.Empty, "nothing saved");
        Assert.That(_shop.Log.Select(logged => logged.Message), Has.None.Contains(key));
    }

    [Test]
    public async Task Verifies_a_key_with_the_real_SDK_which_refuses_an_invalid_one()
    {
        _gateway = new SattlemeGateway(new SdkLogger(Mock.Of<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>()));
        var result = await Controller().Configure(Posted("sattleme_ck_not-a-key"));

        var model = (ConfigurationModel)((ViewResult)result).Model!;
        Assert.That(model.IntegrationKey, Is.Null);
        Assert.That(((ViewResult)result).ViewData.ModelState[nameof(ConfigurationModel.IntegrationKey)]!.Errors.Single().ErrorMessage, Is.EqualTo(Text("Key.Invalid")));
    }

    [TestCase(0)]
    [TestCase(61)]
    [TestCase(-5)]
    public async Task Takes_a_time_to_pay_of_1_to_60_minutes(int minutes)
    {
        var controller = Controller();
        var result = await controller.Configure(Posted(minutes: minutes));

        Assert.That(result, Is.InstanceOf<ViewResult>());
        Assert.That(controller.ModelState[nameof(ConfigurationModel.MinutesToPay)]!.Errors.Single().ErrorMessage, Is.EqualTo(Text("MinutesToPay.Range")));
        Assert.That(_savedProperties, Is.Empty);
    }

    [TestCase(1)]
    [TestCase(60)]
    public async Task Saves_the_time_to_pay(int minutes)
    {
        await Controller().Configure(Posted(minutes: minutes));

        Assert.That(_saved.MinutesToPay, Is.EqualTo(minutes));
    }

    [Test]
    public async Task Offers_the_method_only_with_a_key()
    {
        await Controller().Configure(Posted(enabled: true));
        Assert.That(_paymentSettings.ActivePaymentMethodSystemNames, Is.Empty);
        _notifications.Verify(n => n.ErrorNotification(Text("Enabled.NeedsKey"), It.IsAny<bool>(), It.IsAny<int>()));

        await Controller().Configure(Posted(NEW_KEY, enabled: true));
        Assert.That(_paymentSettings.ActivePaymentMethodSystemNames, Is.EquivalentTo(new[] { SattlemeDefaults.SYSTEM_NAME }));
        _settings.Verify(s => s.SaveSettingAsync(_paymentSettings, It.IsAny<int>()));

        await Controller().Configure(Posted(enabled: false));
        Assert.That(_paymentSettings.ActivePaymentMethodSystemNames, Is.Empty);
    }

    [Test]
    public async Task Removes_the_key_when_asked()
    {
        _shop.Settings[0].EncryptedIntegrationKey = Encryption.Encrypt(NEW_KEY);
        var model = Posted();
        model.RemoveKey = true;
        await Controller().Configure(model);

        Assert.That(_saved.EncryptedIntegrationKey, Is.Empty);
        var page = (ConfigurationModel)((ViewResult)await Controller().Configure()).Model!;
        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.None")));
    }

    [Test]
    public async Task Says_when_the_saved_key_cannot_be_read()
    {
        _shop.Settings[0].EncryptedIntegrationKey = Encryption.Encrypt(NEW_KEY, "an old encryption key");
        var page = (ConfigurationModel)((ViewResult)await Controller().Configure()).Model!;

        Assert.That(page.KeyStatus, Is.EqualTo(Text("Key.Unreadable")));
    }

    [Test]
    public async Task Warns_when_the_store_s_currency_cannot_be_converted()
    {
        _shop.PrimaryCurrency = new Core.Domain.Directory.Currency { Id = 1, CurrencyCode = "XAU" };
        var page = (ConfigurationModel)((ViewResult)await Controller().Configure()).Model!;

        Assert.That(page.CurrencyWarning, Is.EqualTo(Text("Currency.Unsupported")));
    }

    [Test]
    public void Only_admins_who_manage_payment_methods_see_the_page_and_every_change_posts_its_antiforgery_token()
    {
        var type = typeof(SattlemeAdminController);
        Assert.That(type.GetCustomAttribute<AuthorizeAdminAttribute>(), Is.Not.Null);
        Assert.That(type.GetCustomAttribute<AreaAttribute>()!.RouteValue, Is.EqualTo(AreaNames.ADMIN));
        Assert.That(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>(), Is.Not.Null);
        var actions = type.GetMethods().Where(method => method.Name == nameof(SattlemeAdminController.Configure)).ToList();
        Assert.That(actions, Has.Count.EqualTo(2));
        foreach (var action in actions)
        {
            var permission = action.GetCustomAttribute<CheckPermissionAttribute>();
            Assert.That(permission, Is.Not.Null);
            Assert.That(permission!.PermissionSystemName, Is.EqualTo(new[] { StandardPermission.Configuration.MANAGE_PAYMENT_METHODS }));
        }
        Assert.That(actions.Single(action => action.GetParameters().Length == 1).GetCustomAttribute<HttpPostAttribute>(), Is.Not.Null);
    }

    [Test]
    public void The_key_field_is_a_password_field_of_a_model_that_never_holds_the_saved_key()
    {
        var property = typeof(ConfigurationModel).GetProperty(nameof(ConfigurationModel.IntegrationKey))!;
        Assert.That(property.GetCustomAttribute<System.ComponentModel.DataAnnotations.DataTypeAttribute>()!.DataType,
            Is.EqualTo(System.ComponentModel.DataAnnotations.DataType.Password));
        Assert.That(typeof(ConfigurationModel).GetProperties().Select(p => p.Name), Has.None.Contains("Encrypted"));
        var view = File.ReadAllText(Path.Combine(Repository.Plugin, "Views", "Configure.cshtml"));
        Assert.That(view, Does.Contain("<input asp-for=\"IntegrationKey\" type=\"password\""));
    }
}