using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Nop.Plugin.Payments.Sattleme.Controllers;
using Nop.Plugin.Payments.Sattleme.Domain;
using Nop.Plugin.Payments.Sattleme.Models;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Messages;
using Nop.Web.Framework.Mvc.Filters;
using NUnit.Framework;
using Order = Nop.Core.Domain.Orders.Order;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The payment page and what its script asks: authorized by the order's GUID and the page's
/// secret only, never cached nor indexed, checked at most every few seconds, and working without
/// JavaScript.
/// </summary>
[TestFixture]
public sealed class PaymentPageTests
{
    private Shop _shop = null!;
    private Order _order = null!;
    private SattlemeInvoice _invoice = null!;
    private SattlemeOrder _record = null!;
    private Mock<INotificationService> _notifications = null!;

    [SetUp]
    public async Task SetUp()
    {
        _shop = new Shop();
        _order = _shop.AddOrder();
        _invoice = (await _shop.NewScope().Maker.MakeAgainAsync(_order)).Invoice!;
        _record = _shop.Database.Orders().Single();
        _notifications = new Mock<INotificationService>();
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private SattlemePaymentController Controller()
    {
        var scope = _shop.NewScope();
        return new SattlemePaymentController(scope.Messages, _notifications.Object, scope.Maker, scope.OrderService.Object, scope.Checker, scope.Factory, scope.Store)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
    }

    private static void AssertPrivate(Controller controller)
    {
        var headers = controller.Response.Headers;
        Assert.That(headers.CacheControl.ToString(), Does.Contain("no-store"));
        Assert.That(headers["X-Robots-Tag"].ToString(), Does.Contain("noindex"));
        Assert.That(headers["Referrer-Policy"].ToString(), Is.EqualTo("no-referrer"));
    }

    [Test]
    public async Task Shows_the_invoice_with_the_page_s_secret()
    {
        var controller = Controller();
        var result = await controller.Pay(_order.OrderGuid, _record.PageToken, null);

        var model = (PaymentPageModel)((ViewResult)result).Model!;
        Assert.That(((ViewResult)result).ViewName, Is.EqualTo(SattlemeDefaults.VIEWS + "Pay.cshtml"));
        Assert.That(model.Phase, Is.EqualTo("open"));
        Assert.That(model.Uri, Does.StartWith("bitcoin:" + SdkData.ADDRESS));
        Assert.That(model.QrSvg, Does.StartWith("<svg"));
        Assert.That(model.Lightning, Does.StartWith("lnbc"));
        Assert.That(model.Address, Is.EqualTo(SdkData.ADDRESS));
        Assert.That(model.ToPayBtc, Is.EqualTo("0.000245"));
        Assert.That(model.Price, Is.EqualTo("CHF 24.50"));
        Assert.That(model.SecondsLeft, Is.EqualTo(900));
        Assert.That(model.TimeLeft, Is.EqualTo("15:00"));
        Assert.That(model.PollSeconds, Is.EqualTo(3));
        Assert.That(model.CanMakeNew, Is.False);
        Assert.That(model.StatusUrl, Does.Contain(_record.PageToken));
        Assert.That(model.CheckUrl, Does.EndWith("?check=1"));
        AssertPrivate(controller);
        Assert.That(_shop.Gateway.Statuses, Is.Zero, "showing the page checks nothing");
    }

    private static IEnumerable<string?> WrongTokens()
    {
        yield return null;
        yield return "";
        yield return new string('0', 64);
        yield return "a";
    }

    [TestCaseSource(nameof(WrongTokens))]
    public async Task Refuses_any_other_secret(string? token)
    {
        var controller = Controller();
        Assert.That(await controller.Pay(_order.OrderGuid, token!, null), Is.InstanceOf<NotFoundResult>());
        Assert.That(await controller.Status(_order.OrderGuid, token!), Is.InstanceOf<NotFoundResult>());
        Assert.That(await controller.NewInvoice(_order.OrderGuid, token!), Is.InstanceOf<NotFoundResult>());
        Assert.That(_shop.Gateway.Statuses + _shop.Gateway.Creates, Is.EqualTo(1), "only the invoice of the setup");
        AssertPrivate(controller);
    }

    [Test]
    public async Task Refuses_the_secret_of_another_order()
    {
        var other = _shop.AddOrder();
        await _shop.NewScope().Maker.MakeAgainAsync(other);
        var otherToken = _shop.Database.Orders().Single(record => record.OrderGuid == other.OrderGuid).PageToken;

        Assert.That(await Controller().Pay(_order.OrderGuid, otherToken, null), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Refuses_an_order_of_another_payment_method_or_deleted()
    {
        _shop.Edit(_order.Id, order => order.PaymentMethodSystemName = "Payments.CheckMoneyOrder");
        Assert.That(await Controller().Pay(_order.OrderGuid, _record.PageToken, null), Is.InstanceOf<NotFoundResult>());

        _shop.Edit(_order.Id, order =>
        {
            order.PaymentMethodSystemName = SattlemeDefaults.SYSTEM_NAME;
            order.Deleted = true;
        });
        Assert.That(await Controller().Pay(_order.OrderGuid, _record.PageToken, null), Is.InstanceOf<NotFoundResult>());
    }

    [Test]
    public async Task Without_JavaScript_a_button_checks_again_then_shows_the_page()
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        var result = await Controller().Pay(_order.OrderGuid, _record.PageToken, "1");

        var redirect = (RedirectToRouteResult)result;
        Assert.That(redirect.RouteName, Is.EqualTo(SattlemeDefaults.Route.PAY));
        Assert.That(redirect.RouteValues!["orderGuid"], Is.EqualTo(_order.OrderGuid));
        Assert.That(redirect.RouteValues["token"], Is.EqualTo(_record.PageToken));
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));
        Assert.That(_shop.Order(_order.Id).PaymentStatus, Is.EqualTo(Core.Domain.Payments.PaymentStatus.Paid));

        var page = (PaymentPageModel)((ViewResult)await Controller().Pay(_order.OrderGuid, _record.PageToken, null)).Model!;
        Assert.That(page.Phase, Is.EqualTo("paid"));
        Assert.That(page.PollSeconds, Is.Null);
    }

    [Test]
    public async Task The_status_says_where_the_payment_is_and_the_page_s_version()
    {
        var open = (JsonResult)await Controller().Status(_order.OrderGuid, _record.PageToken);
        var (phase, version) = Read(open);
        Assert.That(phase, Is.EqualTo("open"));

        _shop.Time.Advance(SattlemeDefaults.PageInterval);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 0));
        var (later, laterVersion) = Read((JsonResult)await Controller().Status(_order.OrderGuid, _record.PageToken));

        Assert.That(later, Is.EqualTo("unconfirmed"));
        Assert.That(laterVersion, Is.Not.EqualTo(version));
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(2));
    }

    [Test]
    public async Task The_status_checks_at_most_every_few_seconds_whoever_asks()
    {
        for (var i = 0; i < 10; i++)
            Assert.That(await Controller().Status(_order.OrderGuid, _record.PageToken), Is.InstanceOf<JsonResult>());
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(1));

        _shop.Time.Advance(SattlemeDefaults.PageInterval);
        await Controller().Status(_order.OrderGuid, _record.PageToken);
        Assert.That(_shop.Gateway.Statuses, Is.EqualTo(2));
    }

    [Test]
    public async Task Gives_a_new_invoice_once_the_last_one_ended_unpaid()
    {
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await Controller().Status(_order.OrderGuid, _record.PageToken);
        var expired = (PaymentPageModel)((ViewResult)await Controller().Pay(_order.OrderGuid, _record.PageToken, null)).Model!;
        Assert.That(expired.Phase, Is.EqualTo("expired"));
        Assert.That(expired.CanMakeNew, Is.True);

        var result = await Controller().NewInvoice(_order.OrderGuid, _record.PageToken);

        Assert.That(((RedirectToRouteResult)result).RouteName, Is.EqualTo(SattlemeDefaults.Route.PAY));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(2));
        var page = (PaymentPageModel)((ViewResult)await Controller().Pay(_order.OrderGuid, _record.PageToken, null)).Model!;
        Assert.That(page.Phase, Is.EqualTo("open"));
        _notifications.Verify(n => n.ErrorNotification(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task Asked_for_a_new_invoice_while_one_may_be_paid_shows_that_one()
    {
        var result = await Controller().NewInvoice(_order.OrderGuid, _record.PageToken);

        Assert.That(result, Is.InstanceOf<RedirectToRouteResult>());
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        _notifications.Verify(n => n.ErrorNotification(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task Refuses_a_new_invoice_while_one_of_another_amount_may_be_paid_and_says_until_when()
    {
        _shop.Edit(_order.Id, order => order.OrderTotal = 30m);
        var result = await Controller().NewInvoice(_order.OrderGuid, _record.PageToken);

        Assert.That(result, Is.InstanceOf<RedirectToRouteResult>());
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
        var until = DateTimeOffset.FromUnixTimeSeconds(_invoice.ExpiresAt + SattlemeDefaults.GRACE_SECONDS).UtcDateTime.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
        _notifications.Verify(n => n.ErrorNotification(_shop.Texts.Format("Error.Pending", until), It.IsAny<bool>(), It.IsAny<int>()), Times.Once);
    }

    [Test]
    public void Changes_state_only_through_a_form_with_its_antiforgery_token()
    {
        var type = typeof(SattlemePaymentController);
        Assert.That(type.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>(), Is.Not.Null);
        Assert.That(type.GetMethod(nameof(SattlemePaymentController.NewInvoice))!.GetCustomAttribute<HttpPostAttribute>(), Is.Not.Null);
        Assert.That(type.GetMethod(nameof(SattlemePaymentController.Pay))!.GetCustomAttribute<HttpGetAttribute>(), Is.Not.Null);
        Assert.That(type.GetMethod(nameof(SattlemePaymentController.Status))!.GetCustomAttribute<HttpGetAttribute>(), Is.Not.Null);
        // The shop's own checks of its public pages, as nopCommerce's BasePublicController.
        Assert.That(type.GetCustomAttribute<CheckAccessPublicStoreAttribute>(), Is.Not.Null);
        Assert.That(type.GetCustomAttribute<CheckAccessClosedStoreAttribute>(), Is.Not.Null);
        Assert.That(type.GetCustomAttribute<CheckLanguageSeoCodeAttribute>(), Is.Not.Null);
    }

    private static (string Phase, string Version) Read(JsonResult result)
    {
        var value = result.Value!;
        var phase = (string)value.GetType().GetProperty("phase")!.GetValue(value)!;
        var version = (string)value.GetType().GetProperty("version")!.GetValue(value)!;
        return (phase, version);
    }
}