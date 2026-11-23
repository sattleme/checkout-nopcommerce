using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Cms;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Core.Domain.ScheduleTasks;
using Nop.Plugin.Payments.Sattleme.Components;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Cms;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Plugins;
using Nop.Services.ScheduleTasks;
using Nop.Web.Framework.Infrastructure;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.Sattleme;

/// <summary>
/// The payment method (README.md, "How it works"): the invoice is made when the customer places the
/// order (a refusal lets them choose another method), then the customer pays it on a page of the
/// shop, to which nopCommerce sends them after the order is placed (a redirection payment method,
/// as nopCommerce's documentation describes them: PostProcessPaymentAsync redirects). The order's
/// pages show where its payment is (widgets).
/// </summary>
public partial class SattlemePaymentMethod : BasePlugin, IPaymentMethod, IWidgetPlugin
{
    #region Fields

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILocalizationService _localizationService;
    private readonly CustomerMessages _messages;
    private readonly INopUrlHelper _nopUrlHelper;
    private readonly IntegrationKeys _keys;
    private readonly InvoiceMaker _maker;
    private readonly IOrderTotalCalculationService _orderTotalCalculationService;
    private readonly IScheduleTaskService _scheduleTaskService;
    private readonly ISettingService _settingService;
    private readonly IStoreContext _storeContext;
    private readonly IWorkContext _workContext;
    private readonly PaymentSettings _paymentSettings;
    private readonly PluginResources _resources;
    private readonly SattlemeStore _store;
    private readonly TimeProvider _time;
    private readonly WidgetSettings _widgetSettings;

    #endregion

    #region Ctor

    /// <summary>The payment method.</summary>
    public SattlemePaymentMethod(IHttpContextAccessor httpContextAccessor,
        ILocalizationService localizationService,
        CustomerMessages messages,
        INopUrlHelper nopUrlHelper,
        IntegrationKeys keys,
        InvoiceMaker maker,
        IOrderTotalCalculationService orderTotalCalculationService,
        IScheduleTaskService scheduleTaskService,
        ISettingService settingService,
        IStoreContext storeContext,
        IWorkContext workContext,
        PaymentSettings paymentSettings,
        PluginResources resources,
        SattlemeStore store,
        TimeProvider time,
        WidgetSettings widgetSettings)
    {
        _httpContextAccessor = httpContextAccessor;
        _localizationService = localizationService;
        _messages = messages;
        _nopUrlHelper = nopUrlHelper;
        _keys = keys;
        _maker = maker;
        _orderTotalCalculationService = orderTotalCalculationService;
        _scheduleTaskService = scheduleTaskService;
        _settingService = settingService;
        _storeContext = storeContext;
        _workContext = workContext;
        _paymentSettings = paymentSettings;
        _resources = resources;
        _store = store;
        _time = time;
        _widgetSettings = widgetSettings;
    }

    #endregion

    #region Payments

    /// <summary>
    /// Makes the invoice of the order being placed, before nopCommerce stores it: when none can be
    /// made, the order is not placed, and the customer is told why and may choose another method.
    /// </summary>
    public async Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(processPaymentRequest);
        var result = new ProcessPaymentResult { NewPaymentStatus = PaymentStatus.Pending };
        var made = await _maker.MakeForPlacementAsync(processPaymentRequest);
        if (made.Invoice is null)
            result.AddError(await MessageAsync(made));
        return result;
    }

    /// <summary>Sends the customer to the order's payment page, once nopCommerce stored the order.</summary>
    public async Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
    {
        ArgumentNullException.ThrowIfNull(postProcessPaymentRequest);
        var order = postProcessPaymentRequest.Order;
        await _store.SetOrderIdAsync(order.OrderGuid, order.Id);
        var url = await PayUrlAsync(order);
        if (url is not null)
            _httpContextAccessor.HttpContext?.Response.Redirect(url);
    }

    /// <summary>
    /// Hidden while the store has no integration key that can be read, or when its primary currency
    /// is not one the SDK converts (its table of currencies). An amount the SDK refuses (out of the
    /// merchant's limits) is refused when the order is placed, with a message.
    /// </summary>
    public async Task<bool> HidePaymentMethodAsync(IList<ShoppingCartItem> cart)
    {
        var store = await _storeContext.GetCurrentStoreAsync();
        if (await _keys.KeyOfStoreAsync(store.Id) is null)
            return true;
        return await _maker.PriceOfAsync(1m) is null;
    }

    /// <summary>The additional fee, as nopCommerce's own payment methods compute it.</summary>
    public async Task<decimal> GetAdditionalHandlingFeeAsync(IList<ShoppingCartItem> cart)
    {
        var store = await _storeContext.GetCurrentStoreAsync();
        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(store.Id);
        return await _orderTotalCalculationService.CalculatePaymentAdditionalFeeAsync(cart, settings.AdditionalFee, settings.AdditionalFeePercentage);
    }

    /// <summary>Not supported: a Bitcoin payment is paid at once.</summary>
    public Task<CapturePaymentResult> CaptureAsync(CapturePaymentRequest capturePaymentRequest)
    {
        return Task.FromResult(new CapturePaymentResult { Errors = ["Capture method not supported"] });
    }

    /// <summary>
    /// Not supported: a Lightning payment can only be refunded to an invoice of the customer's, an
    /// on-chain one to an address of theirs (README.md, "Refunds").
    /// </summary>
    public Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest refundPaymentRequest)
    {
        return Task.FromResult(new RefundPaymentResult { Errors = ["Refund method not supported"] });
    }

    /// <summary>Not supported.</summary>
    public Task<VoidPaymentResult> VoidAsync(VoidPaymentRequest voidPaymentRequest)
    {
        return Task.FromResult(new VoidPaymentResult { Errors = ["Void method not supported"] });
    }

    /// <summary>Not supported.</summary>
    public Task<ProcessPaymentResult> ProcessRecurringPaymentAsync(ProcessPaymentRequest processPaymentRequest)
    {
        return Task.FromResult(new ProcessPaymentResult { Errors = ["Recurring payment not supported"] });
    }

    /// <summary>Not supported.</summary>
    public Task<CancelRecurringPaymentResult> CancelRecurringPaymentAsync(CancelRecurringPaymentRequest cancelPaymentRequest)
    {
        return Task.FromResult(new CancelRecurringPaymentResult { Errors = ["Recurring payment not supported"] });
    }

    /// <summary>
    /// Whether the order's page offers "Retry payment" (OrderModelFactory, and OrderController's
    /// RePostPayment, which redirects through PostProcessPaymentAsync; nopCommerce checks first that
    /// re-posting is allowed, the method a redirection one, the order pending payment, not deleted
    /// nor cancelled: PaymentService.CanRePostProcessPaymentAsync). It leads to the payment page:
    /// offered only while that page lets the customer pay (an invoice to pay, or a new one to get),
    /// never once a payment is being confirmed or came, nor once an invoice ended with something
    /// received, for which no new invoice is made (PaymentRules.PageTakesPayment).
    /// </summary>
    public async Task<bool> CanRePostProcessPaymentAsync(Order order)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.PaymentMethodSystemName != SattlemeDefaults.SYSTEM_NAME || await _store.GetOrderAsync(order.OrderGuid) is null)
            return false;
        var invoices = await _store.GetInvoicesAsync(order.OrderGuid);
        return PaymentRules.PageTakesPayment(order, invoices.Count == 0 ? null : invoices[^1], _time.GetUtcNow().ToUnixTimeSeconds());
    }

    /// <summary>Nothing to enter.</summary>
    public Task<IList<string>> ValidatePaymentFormAsync(IFormCollection form)
    {
        return Task.FromResult<IList<string>>([]);
    }

    /// <summary>Nothing to enter.</summary>
    public Task<ProcessPaymentRequest> GetPaymentInfoAsync(IFormCollection form)
    {
        return Task.FromResult(new ProcessPaymentRequest());
    }

    /// <summary>What the checkout's payment information step shows: the method's description.</summary>
    public Type GetPublicViewComponent()
    {
        return typeof(PaymentInfoViewComponent);
    }

    /// <summary>The description at checkout: the admin's, in the customer's language, else how a Bitcoin payment goes.</summary>
    public async Task<string> GetPaymentMethodDescriptionAsync()
    {
        var store = await _storeContext.GetCurrentStoreAsync();
        var language = await _workContext.GetWorkingLanguageAsync();
        var settings = await _settingService.LoadSettingAsync<SattlemeSettings>(store.Id);
        var description = await _localizationService.GetLocalizedSettingAsync(settings, setting => setting.DescriptionText, language.Id, store.Id);
        return string.IsNullOrWhiteSpace(description)
            ? await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + "PaymentMethodDescription")
            : description;
    }

    /// <summary>The configuration page.</summary>
    public override string GetConfigurationPageUrl()
    {
        return _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.CONFIGURE);
    }

    /// <summary>Not supported.</summary>
    public bool SupportCapture => false;

    /// <summary>Not supported: see <see cref="RefundAsync"/>.</summary>
    public bool SupportPartiallyRefund => false;

    /// <summary>Not supported: see <see cref="RefundAsync"/>.</summary>
    public bool SupportRefund => false;

    /// <summary>Not supported.</summary>
    public bool SupportVoid => false;

    /// <summary>Not supported.</summary>
    public RecurringPaymentType RecurringPaymentType => RecurringPaymentType.NotSupported;

    /// <summary>The customer is sent to the payment page once the order is placed.</summary>
    public PaymentMethodType PaymentMethodType => PaymentMethodType.Redirection;

    /// <summary>The payment information step shows the description.</summary>
    public bool SkipPaymentInfo => false;

    #endregion

    #region Widgets

    /// <summary>Not in the admin area's list of widgets: the payment method's own.</summary>
    public bool HideInWidgetList => true;

    /// <summary>The order's page in the shop and in the admin area.</summary>
    public Task<IList<string>> GetWidgetZonesAsync()
    {
        return Task.FromResult<IList<string>>([PublicWidgetZones.OrderDetailsPageOverview, AdminWidgetZones.OrderDetailsBlock]);
    }

    /// <summary>The component of <paramref name="widgetZone"/>.</summary>
    public Type GetWidgetViewComponent(string widgetZone)
    {
        ArgumentNullException.ThrowIfNull(widgetZone);
        return widgetZone == AdminWidgetZones.OrderDetailsBlock ? typeof(AdminOrderViewComponent) : typeof(OrderPaymentViewComponent);
    }

    #endregion

    #region Install

    /// <summary>The settings, the texts, the background checks (a scheduled task) and the order pages' widgets.</summary>
    public override async Task InstallAsync()
    {
        await _settingService.SaveSettingAsync(new SattlemeSettings());
        await _resources.InstallAsync();
        if (await _scheduleTaskService.GetTaskByTypeAsync(SattlemeDefaults.CHECK_TASK_TYPE) is null)
        {
            await _scheduleTaskService.InsertTaskAsync(new ScheduleTask
            {
                Name = SattlemeDefaults.CHECK_TASK_NAME,
                Seconds = SattlemeDefaults.CHECK_TASK_SECONDS,
                Type = SattlemeDefaults.CHECK_TASK_TYPE,
                Enabled = true,
                LastEnabledUtc = _time.GetUtcNow().UtcDateTime,
                StopOnError = false,
            });
        }
        if (!_widgetSettings.ActiveWidgetSystemNames.Contains(SattlemeDefaults.SYSTEM_NAME))
        {
            _widgetSettings.ActiveWidgetSystemNames.Add(SattlemeDefaults.SYSTEM_NAME);
            await _settingService.SaveSettingAsync(_widgetSettings);
        }
        await base.InstallAsync();
    }

    /// <summary>
    /// Refuses to be uninstalled while a payment may still come (an invoice that can be paid, or a
    /// payment being confirmed): uninstalling deletes the plugin's records, and their checks.
    /// </summary>
    public override async Task PreparePluginToUninstallAsync()
    {
        var pending = await _store.CountUnsettledAsync();
        if (pending > 0)
            throw new NopException(string.Format(System.Globalization.CultureInfo.CurrentCulture, await _localizationService.GetResourceAsync(SattlemeDefaults.RESOURCE_PREFIX + "Uninstall.Pending"), pending));
        await base.PreparePluginToUninstallAsync();
    }

    /// <summary>The settings, the texts, the scheduled task, the method's and the widgets' activation.</summary>
    public override async Task UninstallAsync()
    {
        await _settingService.DeleteSettingAsync<SattlemeSettings>();
        if (await _scheduleTaskService.GetTaskByTypeAsync(SattlemeDefaults.CHECK_TASK_TYPE) is { } task)
            await _scheduleTaskService.DeleteTaskAsync(task);
        if (_paymentSettings.ActivePaymentMethodSystemNames.Remove(SattlemeDefaults.SYSTEM_NAME))
            await _settingService.SaveSettingAsync(_paymentSettings);
        if (_widgetSettings.ActiveWidgetSystemNames.Remove(SattlemeDefaults.SYSTEM_NAME))
            await _settingService.SaveSettingAsync(_widgetSettings);
        await _resources.UninstallAsync();
        await base.UninstallAsync();
    }

    #endregion

    #region Utilities

    /// <summary>The order's payment page (relative), or null when the order has no Bitcoin payment.</summary>
    private async Task<string?> PayUrlAsync(Order order)
    {
        var record = await _store.GetOrderAsync(order.OrderGuid);
        return record is null
            ? null
            : _nopUrlHelper.RouteUrl(SattlemeDefaults.Route.PAY, new { orderGuid = order.OrderGuid, token = record.PageToken });
    }

    /// <summary>Why no invoice is made, in the customer's language.</summary>
    private Task<string> MessageAsync(MakeResult made)
    {
        return _messages.RefusalAsync(made);
    }

    #endregion
}