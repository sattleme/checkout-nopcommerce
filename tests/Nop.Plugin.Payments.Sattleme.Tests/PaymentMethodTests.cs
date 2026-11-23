using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;
using Nop.Core;
using Nop.Core.Domain.Cms;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.ScheduleTasks;
using Nop.Plugin.Payments.Sattleme.Components;
using Nop.Plugin.Payments.Sattleme.Infrastructure;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Plugin.Payments.Sattleme.Tests.Support;
using Nop.Services.Messages;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.ScheduleTasks;
using Nop.Web.Framework.Events;
using Nop.Web.Framework.Infrastructure;
using Nop.Web.Framework.Models;
using NopSattleme.Sattleme.Checkout;
using NUnit.Framework;
using PaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Tests;

/// <summary>
/// The payment method as nopCommerce calls it: offered or not, the invoice made when the order is
/// placed, the customer sent to the payment page, refunds never automatic, the customer kept from
/// cancelling an order a payment may still come for or something came for (its page offering no
/// "Cancel order" then), the plugin kept installed while a payment may come.
/// </summary>
[TestFixture]
public sealed class PaymentMethodTests
{
    private Shop _shop = null!;
    private Scope _scope = null!;
    private DefaultHttpContext _http = null!;
    private PaymentSettings _paymentSettings = null!;
    private WidgetSettings _widgetSettings = null!;
    private Mock<IScheduleTaskService> _tasks = null!;
    private Mock<PluginResources> _resources = null!;

    [SetUp]
    public void SetUp()
    {
        _shop = new Shop();
        _scope = _shop.NewScope();
        _http = new DefaultHttpContext();
        _paymentSettings = new PaymentSettings { ActivePaymentMethodSystemNames = [SattlemeDefaults.SYSTEM_NAME] };
        _widgetSettings = new WidgetSettings { ActiveWidgetSystemNames = [] };
        _tasks = new Mock<IScheduleTaskService>();
        _resources = new Mock<PluginResources>(_scope.LanguageService.Object, _scope.Localization.Object, null!);
    }

    [TearDown]
    public void TearDown()
    {
        _shop.Dispose();
    }

    private SattlemePaymentMethod Method()
    {
        var accessor = new Mock<IHttpContextAccessor>();
        accessor.Setup(a => a.HttpContext).Returns(_http);
        var totals = new Mock<IOrderTotalCalculationService>();
        totals.Setup(t => t.CalculatePaymentAdditionalFeeAsync(It.IsAny<IList<ShoppingCartItem>>(), It.IsAny<decimal>(), It.IsAny<bool>()))
            .ReturnsAsync((IList<ShoppingCartItem> _, decimal fee, bool percentage) => percentage ? fee * 2 : fee);
        return new SattlemePaymentMethod(accessor.Object, _scope.Localization.Object, _scope.Messages, _scope.UrlHelper.Object, _scope.Keys, _scope.Maker,
            totals.Object, _tasks.Object, _scope.SettingService.Object, _scope.StoreContext.Object, _scope.WorkContext.Object, _paymentSettings,
            _resources.Object, _scope.Store, _shop.Time, _widgetSettings);
    }

    [Test]
    public async Task Is_offered_with_a_key_and_a_currency_the_SDK_converts()
    {
        Assert.That(await Method().HidePaymentMethodAsync([]), Is.False);
    }

    [Test]
    public async Task Is_hidden_without_a_key_that_can_be_read()
    {
        _shop.Settings[0].EncryptedIntegrationKey = "";
        Assert.That(await Method().HidePaymentMethodAsync([]), Is.True);
        _shop.Settings[0].EncryptedIntegrationKey = Encryption.Encrypt(SdkData.KEY, "another key");
        Assert.That(await Method().HidePaymentMethodAsync([]), Is.True);
    }

    [Test]
    public async Task Is_hidden_in_a_currency_the_SDK_does_not_convert()
    {
        _shop.PrimaryCurrency = new Core.Domain.Directory.Currency { Id = 1, CurrencyCode = "XAU" };
        Assert.That(await Method().HidePaymentMethodAsync([]), Is.True);
    }

    [Test]
    public async Task Uses_a_store_s_own_key()
    {
        _shop.Settings[0].EncryptedIntegrationKey = "";
        _shop.Settings[1] = new SattlemeSettings { EncryptedIntegrationKey = Encryption.Encrypt(SdkData.KEY) };
        Assert.That(await Method().HidePaymentMethodAsync([]), Is.False);
    }

    [Test]
    public async Task Charges_the_additional_fee_as_nopCommerce_s_own_methods()
    {
        _shop.Settings[0].AdditionalFee = 1.5m;
        Assert.That(await Method().GetAdditionalHandlingFeeAsync([]), Is.EqualTo(1.5m));
        _shop.Settings[0].AdditionalFeePercentage = true;
        Assert.That(await Method().GetAdditionalHandlingFeeAsync([]), Is.EqualTo(3m));
    }

    [Test]
    public async Task Makes_the_invoice_when_the_order_is_placed()
    {
        var result = await Method().ProcessPaymentAsync(new ProcessPaymentRequest { OrderGuid = Guid.NewGuid(), OrderTotal = 24.50m, StoreId = 1, CustomerId = 7 });

        Assert.That(result.Success, Is.True);
        Assert.That(result.NewPaymentStatus, Is.EqualTo(PaymentStatus.Pending));
        Assert.That(_shop.Gateway.Creates, Is.EqualTo(1));
    }

    [Test]
    public async Task Refuses_the_order_with_a_message_when_no_invoice_can_be_made()
    {
        _shop.Gateway.Create = (_, _) => throw new InvalidOrderException("fiat", "above the merchant's maximum");
        var result = await Method().ProcessPaymentAsync(new ProcessPaymentRequest { OrderGuid = Guid.NewGuid(), OrderTotal = 24.50m, StoreId = 1, CustomerId = 7 });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Errors, Is.EqualTo(new[] { _shop.Texts.Format("Error.NotPayable") }));
    }

    [Test]
    public async Task Sends_the_customer_to_the_payment_page_once_the_order_is_stored()
    {
        var order = _shop.AddOrder();
        await Method().ProcessPaymentAsync(new ProcessPaymentRequest { OrderGuid = order.OrderGuid, OrderTotal = order.OrderTotal, StoreId = 1, CustomerId = 7 });
        await Method().PostProcessPaymentAsync(new PostProcessPaymentRequest { Order = order });

        var record = _shop.Database.Orders().Single();
        Assert.That(record.OrderId, Is.EqualTo(order.Id));
        Assert.That(_http.Response.StatusCode, Is.EqualTo(302));
        Assert.That(_http.Response.Headers.Location.ToString(), Is.EqualTo(Scope.Url(SattlemeDefaults.Route.PAY, new { orderGuid = order.OrderGuid, token = record.PageToken })));
        Assert.That(Method().PaymentMethodType, Is.EqualTo(PaymentMethodType.Redirection));
        Assert.That(await Method().CanRePostProcessPaymentAsync(order), Is.True);
    }

    private async Task<bool> RetryOfferedAsync(Core.Domain.Orders.Order order)
    {
        return await Method().CanRePostProcessPaymentAsync(_shop.Order(order.Id));
    }

    private Task<bool> CheckAsync(Domain.SattlemeInvoice invoice, NopSattleme.Sattleme.Checkout.PaymentStatus status)
    {
        _shop.Gateway.Status = (_, _) => Task.FromResult(status);
        return _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Offers_retry_payment_while_the_payment_page_lets_the_customer_pay()
    {
        var order = _shop.AddOrder();
        Assert.That(await RetryOfferedAsync(order), Is.False, "no payment page: the order is not the plugin's yet");
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        Assert.That(await RetryOfferedAsync(order), Is.True, "an invoice to pay");
        await CheckAsync(invoice, SdkData.OpenWithRest(invoice.ExpiresAt, 5_000, 19_500));
        Assert.That(await RetryOfferedAsync(order), Is.True, "the rest to pay");
        _shop.Time.Advance(TimeSpan.FromMinutes(16));
        Assert.That(await RetryOfferedAsync(order), Is.True, "its time over, being checked: a new invoice may follow");

        var unpaid = _shop.AddOrder();
        var expired = (await _shop.NewScope().Maker.MakeAgainAsync(unpaid)).Invoice!;
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await CheckAsync(expired, SdkData.Expired());
        Assert.That(await RetryOfferedAsync(unpaid), Is.True, "ended unpaid: a new invoice");
    }

    [Test]
    public async Task Offers_no_retry_payment_where_it_leads_nowhere()
    {
        // An underpayment that ended: no new invoice is made, the merchant settles the order.
        var underpaid = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(underpaid)).Invoice!;
        await CheckAsync(invoice, SdkData.OpenWithRest(invoice.ExpiresAt, 5_000, 19_500));
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await CheckAsync(invoice, SdkData.Expired(5_000));
        Assert.That(await RetryOfferedAsync(underpaid), Is.False);
        Assert.That((await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(underpaid.Id))).Refusal, Is.EqualTo("Error.Underpaid"), "as the page refuses");

        // Being confirmed; paid, the order's total changed since (the merchant checks it).
        var confirming = _shop.AddOrder();
        invoice = (await _shop.NewScope().Maker.MakeAgainAsync(confirming)).Invoice!;
        await CheckAsync(invoice, SdkData.Unconfirmed(24_500, 0));
        Assert.That(await RetryOfferedAsync(confirming), Is.False);
        _shop.Edit(confirming.Id, stored => stored.OrderTotal = 30m);
        await CheckAsync(invoice, SdkData.PaidOnchain(24_500));
        Assert.That(_shop.Order(confirming.Id).PaymentStatus, Is.EqualTo(PaymentStatus.Pending));
        Assert.That(await RetryOfferedAsync(confirming), Is.False);

        // Paid.
        var paid = _shop.AddOrder();
        invoice = (await _shop.NewScope().Maker.MakeAgainAsync(paid)).Invoice!;
        await CheckAsync(invoice, SdkData.PaidLightning());
        Assert.That(await RetryOfferedAsync(paid), Is.False);

        // Another method's order.
        var other = _shop.AddOrder();
        await _shop.NewScope().Maker.MakeAgainAsync(other);
        _shop.Edit(other.Id, stored => stored.PaymentMethodSystemName = "Payments.CheckMoneyOrder");
        Assert.That(await RetryOfferedAsync(other), Is.False);
    }

    [Test]
    public async Task Never_refunds_captures_or_voids_by_itself()
    {
        var method = Method();
        Assert.That(method.SupportRefund, Is.False);
        Assert.That(method.SupportPartiallyRefund, Is.False);
        Assert.That(method.SupportCapture, Is.False);
        Assert.That(method.SupportVoid, Is.False);
        Assert.That(method.RecurringPaymentType, Is.EqualTo(RecurringPaymentType.NotSupported));
        Assert.That((await method.RefundAsync(new RefundPaymentRequest())).Success, Is.False);
        Assert.That((await method.CaptureAsync(new CapturePaymentRequest())).Success, Is.False);
        Assert.That((await method.VoidAsync(new VoidPaymentRequest())).Success, Is.False);
        Assert.That(_shop.Gateway.Creates + _shop.Gateway.Statuses, Is.Zero);
    }

    [Test]
    public async Task Describes_itself_with_the_admin_s_text_or_its_own()
    {
        Assert.That(await Method().GetPaymentMethodDescriptionAsync(), Is.EqualTo(_shop.Texts.Format("PaymentMethodDescription")));
        _scope.Localization.Setup(l => l.GetLocalizedSettingAsync(It.IsAny<SattlemeSettings>(), It.IsAny<Expression<Func<SattlemeSettings, string>>>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .ReturnsAsync("Pay with <b>bitcoin</b>");
        Assert.That(await Method().GetPaymentMethodDescriptionAsync(), Is.EqualTo("Pay with <b>bitcoin</b>"), "as text: nopCommerce's view encodes it");
    }

    [Test]
    public async Task Shows_its_blocks_on_the_order_s_pages()
    {
        var method = Method();
        Assert.That(await method.GetWidgetZonesAsync(), Is.EquivalentTo(new[] { PublicWidgetZones.OrderDetailsPageOverview, AdminWidgetZones.OrderDetailsBlock }));
        Assert.That(method.GetWidgetViewComponent(AdminWidgetZones.OrderDetailsBlock), Is.EqualTo(typeof(AdminOrderViewComponent)));
        Assert.That(method.GetWidgetViewComponent(PublicWidgetZones.OrderDetailsPageOverview), Is.EqualTo(typeof(OrderPaymentViewComponent)));
        Assert.That(method.GetPublicViewComponent(), Is.EqualTo(typeof(PaymentInfoViewComponent)));
        Assert.That(method.HideInWidgetList, Is.True);
    }

    [Test]
    public async Task Installs_its_settings_texts_background_checks_and_blocks()
    {
        await Method().InstallAsync();

        _scope.SettingService.Verify(s => s.SaveSettingAsync(It.Is<SattlemeSettings>(settings => settings.MinutesToPay == 15 && settings.EncryptedIntegrationKey == ""), It.IsAny<int>()));
        _resources.Verify(r => r.InstallAsync(), Times.Once);
        _tasks.Verify(t => t.InsertTaskAsync(It.Is<ScheduleTask>(task => task.Type == SattlemeDefaults.CHECK_TASK_TYPE && task.Seconds == 60 && task.Enabled && !task.StopOnError)));
        Assert.That(_widgetSettings.ActiveWidgetSystemNames, Does.Contain(SattlemeDefaults.SYSTEM_NAME));
    }

    [Test]
    public async Task Stays_installed_while_a_payment_may_still_come()
    {
        var order = _shop.AddOrder();
        await _scope.Maker.MakeAgainAsync(order);

        var refused = await Assert.ThrowsAsync<NopException>(() => Method().PreparePluginToUninstallAsync());
        Assert.That(refused!.Message, Is.EqualTo(_shop.Texts.Format("Uninstall.Pending", 1)));

        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(_shop.Database.Invoices().Single().Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        await Assert.DoesNotThrowAsync(() => Method().PreparePluginToUninstallAsync());
    }

    [Test]
    public async Task Uninstalls_everything_it_installed()
    {
        _tasks.Setup(t => t.GetTaskByTypeAsync(SattlemeDefaults.CHECK_TASK_TYPE)).ReturnsAsync(new ScheduleTask { Type = SattlemeDefaults.CHECK_TASK_TYPE });
        _widgetSettings.ActiveWidgetSystemNames.Add(SattlemeDefaults.SYSTEM_NAME);
        await Method().UninstallAsync();

        _scope.SettingService.Verify(s => s.DeleteSettingAsync<SattlemeSettings>());
        _tasks.Verify(t => t.DeleteTaskAsync(It.IsAny<ScheduleTask>()));
        _resources.Verify(r => r.UninstallAsync());
        Assert.That(_paymentSettings.ActivePaymentMethodSystemNames, Is.Empty);
        Assert.That(_widgetSettings.ActiveWidgetSystemNames, Is.Empty);
    }

    private (CancelOrderFilter Filter, Mock<INotificationService> Notifications) Filter()
    {
        var notifications = new Mock<INotificationService>();
        return (new CancelOrderFilter(_scope.Localization.Object, notifications.Object, _scope.OrderService.Object, _scope.Store), notifications);
    }

    private static ActionExecutingContext CancelOrder(int orderId, Type controller)
    {
        var action = new ControllerActionDescriptor { ActionName = CancelOrderFilter.ACTION, ControllerTypeInfo = controller.GetTypeInfo() };
        var context = new ActionContext(new DefaultHttpContext(), new RouteData(), action);
        return new ActionExecutingContext(context, [], new Dictionary<string, object?> { ["orderId"] = orderId }, new object());
    }

    private static async Task<bool> RunAsync(CancelOrderFilter filter, ActionExecutingContext context)
    {
        var ran = false;
        await filter.OnActionExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(context, [], new object()));
        });
        return ran;
    }

    [Test]
    public async Task Keeps_the_customer_from_cancelling_an_order_a_payment_may_still_come_for()
    {
        var order = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        var (filter, notifications) = Filter();

        var context = CancelOrder(order.Id, typeof(Web.Controllers.OrderController));
        Assert.That(await RunAsync(filter, context), Is.False);
        Assert.That(((RedirectToRouteResult)context.Result!).RouteValues!["orderId"], Is.EqualTo(order.Id));
        notifications.Verify(n => n.ErrorNotification(_shop.Texts.Format("Cancel.Refused"), It.IsAny<bool>(), It.IsAny<int>()));

        // Being confirmed, past its end: still kept.
        _shop.Time.Advance(TimeSpan.FromMinutes(30));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Unconfirmed(24_500, 1));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.False);

        // Its time over, its final status not known yet: still kept.
        _shop.Gateway.Status = (_, _) => throw new NopSattleme.Sattleme.Checkout.NetworkException("down");
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.False);

        // Ended unpaid: the customer may cancel.
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.True);
    }

    [Test]
    public async Task Keeps_the_customer_from_cancelling_an_order_paid_whose_total_changed()
    {
        var order = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Edit(order.Id, stored => stored.OrderTotal = 30m);
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.PaidLightning());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(_shop.Database.Invoices().Single().Final, Is.True);

        var (filter, notifications) = Filter();
        Assert.That(await RunAsync(filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.False);
        notifications.Verify(n => n.ErrorNotification(_shop.Texts.Format("Cancel.Received"), It.IsAny<bool>(), It.IsAny<int>()));
    }

    [Test]
    public async Task Keeps_the_customer_from_cancelling_an_order_whose_underpayment_reached_the_wallet()
    {
        // 5 000 of 24 500 sats came on-chain, then the invoice ended: expired, final, the sats in the
        // merchant's wallet. The merchant settles the order: the customer cannot cancel it.
        var order = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.OpenWithRest(invoice.ExpiresAt, 5_000, 19_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var (filter, notifications) = Filter();
        Assert.That(await RunAsync(filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.False);
        notifications.Verify(n => n.ErrorNotification(_shop.Texts.Format("Cancel.Refused"), It.IsAny<bool>(), It.IsAny<int>()));

        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var stored = _shop.Database.Invoices().Single();
        Assert.That(stored.Final, Is.True);
        Assert.That(PaymentRules.MayBePaid(stored), Is.False);

        (filter, notifications) = Filter();
        var context = CancelOrder(order.Id, typeof(Web.Controllers.OrderController));
        Assert.That(await RunAsync(filter, context), Is.False);
        Assert.That(((RedirectToRouteResult)context.Result!).RouteValues!["orderId"], Is.EqualTo(order.Id));
        notifications.Verify(n => n.ErrorNotification(_shop.Texts.Format("Cancel.Received"), It.IsAny<bool>(), It.IsAny<int>()));
        Assert.That(_shop.Order(order.Id).OrderStatus, Is.EqualTo(OrderStatus.Pending));
    }

    [Test]
    public async Task Lets_the_customer_cancel_once_every_invoice_ended_with_nothing_received()
    {
        var order = _shop.AddOrder();
        var first = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(first.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        var second = (await _shop.NewScope().Maker.MakeAgainAsync(_shop.Order(order.Id))).Invoice!;
        Assert.That(second.Id, Is.Not.EqualTo(first.Id));
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.False, "the new invoice may be paid");

        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        await _shop.NewScope().Checker.CheckAsync(second.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Controllers.OrderController))), Is.True);
    }

    private OrderDetailsConsumer Consumer()
    {
        return new OrderDetailsConsumer(_scope.OrderService.Object, _scope.Store);
    }

    private async Task<bool> CancelShownAsync(BaseNopModel model)
    {
        await Consumer().HandleEventAsync(new ModelPreparedEvent<BaseNopModel>(model));
        return model is Web.Models.Order.OrderDetailsModel page ? page.CanCancelOrder : throw new InvalidOperationException();
    }

    [Test]
    public async Task Hides_cancel_order_on_the_order_s_page_whenever_the_filter_would_refuse_it()
    {
        var order = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        Assert.That(await CancelShownAsync(new Web.Models.Order.OrderDetailsModel { Id = order.Id, CanCancelOrder = true }), Is.False, "its invoice can be paid");

        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.OpenWithRest(invoice.ExpiresAt, 5_000, 19_500));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired(5_000));
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(_shop.Database.Invoices().Single().Final, Is.True);
        Assert.That(await CancelShownAsync(new Web.Models.Order.OrderDetailsModel { Id = order.Id, CanCancelOrder = true }), Is.False, "an underpayment reached the wallet");
    }

    [Test]
    public async Task Leaves_cancel_order_shown_when_the_customer_may_cancel()
    {
        var order = _shop.AddOrder();
        var invoice = (await _scope.Maker.MakeAgainAsync(order)).Invoice!;
        _shop.Time.Advance(TimeSpan.FromMinutes(20));
        _shop.Gateway.Status = (_, _) => Task.FromResult(SdkData.Expired());
        await _shop.NewScope().Checker.CheckAsync(invoice.Id, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        Assert.That(await CancelShownAsync(new Web.Models.Order.OrderDetailsModel { Id = order.Id, CanCancelOrder = true }), Is.True, "ended unpaid");

        // Another method's order, an order nopCommerce does not let be cancelled, another model: untouched.
        var other = _shop.AddOrder();
        await _shop.NewScope().Maker.MakeAgainAsync(other);
        _shop.Edit(other.Id, stored => stored.PaymentMethodSystemName = "Payments.CheckMoneyOrder");
        Assert.That(await CancelShownAsync(new Web.Models.Order.OrderDetailsModel { Id = other.Id, CanCancelOrder = true }), Is.True);
        var paying = _shop.AddOrder();
        await _shop.NewScope().Maker.MakeAgainAsync(paying);
        Assert.That(await CancelShownAsync(new Web.Models.Order.OrderDetailsModel { Id = paying.Id, CanCancelOrder = false }), Is.False);
        // Any other model of the shop goes no further than its type (no order read).
        var strict = new OrderDetailsConsumer(new Mock<IOrderService>(MockBehavior.Strict).Object, _scope.Store);
        await strict.HandleEventAsync(new ModelPreparedEvent<BaseNopModel>(new Models.OrderPaymentModel { Phase = "open" }));
        await strict.HandleEventAsync(new ModelPreparedEvent<BaseNopModel>(new Models.AdminInvoiceModel()));
    }

    [Test]
    public async Task Leaves_other_actions_and_other_orders_alone()
    {
        var order = _shop.AddOrder();
        await _scope.Maker.MakeAgainAsync(order);
        var other = _shop.AddOrder();
        _shop.Edit(other.Id, stored => stored.PaymentMethodSystemName = "Payments.CheckMoneyOrder");

        Assert.That(await RunAsync(Filter().Filter, CancelOrder(order.Id, typeof(Web.Areas.Admin.Controllers.OrderController))), Is.True, "the admin may cancel: the order's page warns");
        Assert.That(await RunAsync(Filter().Filter, CancelOrder(other.Id, typeof(Web.Controllers.OrderController))), Is.True);
    }
}