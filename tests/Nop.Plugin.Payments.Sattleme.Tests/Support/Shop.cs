using System.Globalization;
using System.Reflection;
using System.Xml.Linq;
using Moq;
using Nop.Core;
using Nop.Core.Domain.Customers;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Localization;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Stores;
using Nop.Plugin.Payments.Sattleme.Factories;
using Nop.Plugin.Payments.Sattleme.Services;
using Nop.Services.Catalog;
using Nop.Services.Configuration;
using Nop.Services.Directory;
using Nop.Services.Helpers;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Security;
using Nop.Services.Stores;
using Nop.Web.Framework.Mvc.Routing;
using NopPaymentStatus = Nop.Core.Domain.Payments.PaymentStatus;

namespace Nop.Plugin.Payments.Sattleme.Tests.Support;

/// <summary>
/// A nopCommerce shop as the plugin sees it: its orders and their notes, its settings, its texts
/// (the plugin's own language packs), its log, its clock, and the SDK faked. Each request is a
/// <see cref="Scope"/> of its own, with its own connection to the shop's database, as in
/// nopCommerce, where the plugin's services live for a request.
/// </summary>
public sealed class Shop : IDisposable
{
    private readonly object _lock = new();
    private readonly List<Order> _orders = [];
    private int _markedPaid;

    /// <summary>A shop whose store 1 has a key, in CHF.</summary>
    public Shop()
    {
        Time = new ManualTime();
        Gateway = new FakeGateway(Time);
        Database = new TestDatabase();
        Settings[0] = new SattlemeSettings { EncryptedIntegrationKey = Encryption.Encrypt(SdkData.KEY) };
        Texts = Texts.Of("en");
    }

    /// <summary>The clock.</summary>
    public ManualTime Time { get; }

    /// <summary>The SDK.</summary>
    public FakeGateway Gateway { get; }

    /// <summary>The plugin's tables.</summary>
    public TestDatabase Database { get; }

    /// <summary>The plugin's settings, per store (0: every store's).</summary>
    public Dictionary<int, SattlemeSettings> Settings { get; } = [];

    /// <summary>The store's primary currency.</summary>
    public Currency PrimaryCurrency { get; set; } = new() { Id = 1, CurrencyCode = "CHF", Name = "Franc" };

    /// <summary>The texts of the working (and the admin's) language.</summary>
    public Texts Texts { get; set; }

    /// <summary>The order notes written.</summary>
    public List<OrderNote> Notes { get; } = [];

    /// <summary>What was logged: its level and message.</summary>
    public List<(string Level, string Message)> Log { get; } = [];

    /// <summary>The customer of the requests.</summary>
    public Customer Customer { get; set; } = new() { Id = 7 };

    /// <summary>How many times nopCommerce's MarkOrderAsPaidAsync ran.</summary>
    public int MarkedPaid => Volatile.Read(ref _markedPaid);

    /// <summary>The notes' texts, in order.</summary>
    public IList<string> NoteTexts
    {
        get
        {
            lock (_lock)
                return [.. Notes.Select(note => note.Note)];
        }
    }

    /// <summary>The log's messages of <paramref name="level"/>.</summary>
    public IList<string> Logged(string level)
    {
        lock (_lock)
            return [.. Log.Where(entry => entry.Level == level).Select(entry => entry.Message)];
    }

    /// <summary>An order placed with the plugin, pending payment, stored as nopCommerce stores it.</summary>
    public Order AddOrder(decimal total = 24.50m, Guid? orderGuid = null)
    {
        lock (_lock)
        {
            var order = new Order
            {
                Id = _orders.Count + 100,
                OrderGuid = orderGuid ?? Guid.NewGuid(),
                CustomOrderNumber = (_orders.Count + 100).ToString(CultureInfo.InvariantCulture),
                StoreId = 1,
                CustomerId = Customer.Id,
                OrderTotal = total,
                PaymentMethodSystemName = SattlemeDefaults.SYSTEM_NAME,
                OrderStatus = OrderStatus.Pending,
                PaymentStatus = NopPaymentStatus.Pending,
                CreatedOnUtc = Time.UtcNow,
            };
            _orders.Add(order);
            return Copy(order);
        }
    }

    /// <summary>The order <paramref name="id"/> as stored now.</summary>
    public Order Order(int id)
    {
        lock (_lock)
            return Copy(_orders.Single(order => order.Id == id));
    }

    /// <summary>Changes the stored order <paramref name="id"/> (an admin's edit).</summary>
    public void Edit(int id, Action<Order> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        lock (_lock)
            edit(_orders.Single(order => order.Id == id));
    }

    /// <summary>A request.</summary>
    public Scope NewScope()
    {
        return new Scope(this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Database.Dispose();
    }

    #region nopCommerce's services

    internal Mock<IOrderService> OrderService()
    {
        var service = new Mock<IOrderService>();
        service.Setup(s => s.GetOrderByGuidAsync(It.IsAny<Guid>())).ReturnsAsync((Guid guid) =>
        {
            lock (_lock)
                return _orders.FirstOrDefault(order => order.OrderGuid == guid) is { } found ? Copy(found) : null!;
        });
        service.Setup(s => s.GetOrderByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) =>
        {
            lock (_lock)
                return _orders.FirstOrDefault(order => order.Id == id) is { } found ? Copy(found) : null!;
        });
        service.Setup(s => s.UpdateOrderAsync(It.IsAny<Order>())).Returns((Order order) =>
        {
            Store(order);
            return Task.CompletedTask;
        });
        service.Setup(s => s.InsertOrderNoteAsync(It.IsAny<OrderNote>())).Returns((OrderNote note) =>
        {
            lock (_lock)
                Notes.Add(note);
            return Task.CompletedTask;
        });
        return service;
    }

    /// <summary>nopCommerce's order processing: CanMarkOrderAsPaid and MarkOrderAsPaidAsync as OrderProcessingService.cs (5.00.1) has them.</summary>
    internal Mock<IOrderProcessingService> OrderProcessing()
    {
        var service = new Mock<IOrderProcessingService>();
        service.Setup(s => s.CanMarkOrderAsPaid(It.IsAny<Order>())).Returns((Order order) => CanMarkOrderAsPaid(order));
        service.Setup(s => s.MarkOrderAsPaidAsync(It.IsAny<Order>())).Returns((Order order) =>
        {
            if (!CanMarkOrderAsPaid(order))
                throw new NopException("You can't mark this order as paid");
            order.PaymentStatus = NopPaymentStatus.Paid;
            order.PaidDateUtc = Time.UtcNow;
            if (order.OrderStatus == OrderStatus.Pending)
                order.OrderStatus = OrderStatus.Processing;
            Store(order);
            Interlocked.Increment(ref _markedPaid);
            lock (_lock)
                Notes.Add(new OrderNote { OrderId = order.Id, Note = "Order has been marked as paid" });
            return Task.CompletedTask;
        });
        return service;
    }

    internal Mock<ILogger> Logger()
    {
        var logger = new Mock<ILogger>();
        logger.Setup(l => l.WarningAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<Customer>()))
            .Returns((string message, Exception _, Customer _) => Write("warning", message));
        logger.Setup(l => l.ErrorAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<Customer>()))
            .Returns((string message, Exception _, Customer _) => Write("error", message));
        logger.Setup(l => l.InformationAsync(It.IsAny<string>(), It.IsAny<Exception>(), It.IsAny<Customer>()))
            .Returns((string message, Exception _, Customer _) => Write("information", message));
        return logger;
    }

    internal Mock<ISettingService> SettingService()
    {
        var service = new Mock<ISettingService>();
        service.Setup(s => s.LoadSettingAsync<SattlemeSettings>(It.IsAny<int>())).ReturnsAsync((int storeId) =>
        {
            lock (_lock)
                return Copy(Settings.TryGetValue(storeId, out var own) ? own : Settings[0]);
        });
        return service;
    }

    internal Mock<ILocalizationService> Localization()
    {
        var service = new Mock<ILocalizationService>();
        service.Setup(s => s.GetResourceAsync(It.IsAny<string>())).ReturnsAsync((string name) => Texts.Get(name) ?? name);
        service.Setup(s => s.GetResourceAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync((string name, int _, bool _, string defaultValue, bool empty) => Texts.Get(name) ?? (empty ? "" : string.IsNullOrEmpty(defaultValue) ? name : defaultValue));
        return service;
    }

    #endregion

    #region Utilities

    private static bool CanMarkOrderAsPaid(Order order)
    {
        return order.OrderStatus != OrderStatus.Cancelled
            && order.PaymentStatus is not (NopPaymentStatus.Paid or NopPaymentStatus.Refunded or NopPaymentStatus.Voided);
    }

    private void Store(Order order)
    {
        lock (_lock)
        {
            var index = _orders.FindIndex(stored => stored.Id == order.Id);
            _orders[index] = Copy(order);
        }
    }

    private Task Write(string level, string message)
    {
        lock (_lock)
            Log.Add((level, message));
        return Task.CompletedTask;
    }

    /// <summary>A copy, as a database read gives one: what a request changes is stored only when it saves it.</summary>
    internal static T Copy<T>(T source)
        where T : new()
    {
        var copy = new T();
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.CanRead && property.CanWrite))
            property.SetValue(copy, property.GetValue(source));
        return copy;
    }

    #endregion
}

/// <summary>One request: the plugin's services, as nopCommerce's container gives them to it.</summary>
public sealed class Scope
{
    internal Scope(Shop shop)
    {
        Shop = shop;
        OrderService = shop.OrderService();
        OrderProcessing = shop.OrderProcessing();
        Logger = shop.Logger();
        SettingService = shop.SettingService();
        Localization = shop.Localization();
        Encryption = new Mock<IEncryptionService>();
        Encryption.Setup(e => e.EncryptText(It.IsAny<string>(), It.IsAny<string>())).Returns((string text, string key) => Support.Encryption.Encrypt(text, key));
        Encryption.Setup(e => e.DecryptText(It.IsAny<string>(), It.IsAny<string>())).Returns((string text, string key) => Support.Encryption.Decrypt(text, key));
        CurrencyService = new Mock<ICurrencyService>();
        CurrencyService.Setup(c => c.GetCurrencyByIdAsync(It.IsAny<int>())).ReturnsAsync((int id) => id == shop.PrimaryCurrency.Id ? shop.PrimaryCurrency : null!);
        CurrencyService.Setup(c => c.GetCurrencyByCodeAsync(It.IsAny<string>())).ReturnsAsync((string code) => code == shop.PrimaryCurrency.CurrencyCode ? shop.PrimaryCurrency : null!);
        StoreService = new Mock<IStoreService>();
        StoreService.Setup(s => s.GetStoreByIdAsync(It.IsAny<int>())).ReturnsAsync(new Store { Id = 1, Name = "Sunrise Bakery" });
        LanguageService = new Mock<ILanguageService>();
        LanguageService.Setup(l => l.GetLanguageByIdAsync(It.IsAny<int>())).ReturnsAsync(new Language { Id = 1, LanguageCulture = "en-US", UniqueSeoCode = "en", Name = "English" });
        LanguageService.Setup(l => l.GetAllLanguagesAsync(It.IsAny<bool>(), It.IsAny<int>())).ReturnsAsync([new Language { Id = 1, LanguageCulture = "en-US", UniqueSeoCode = "en", Name = "English" }]);
        DateTimeHelper = new Mock<IDateTimeHelper>();
        DateTimeHelper.Setup(d => d.ConvertToUserTimeAsync(It.IsAny<DateTime>(), It.IsAny<DateTimeKind>())).ReturnsAsync((DateTime time, DateTimeKind _) => time);
        PriceFormatter = new Mock<IPriceFormatter>();
        PriceFormatter.Setup(p => p.FormatPriceAsync(It.IsAny<decimal>(), It.IsAny<bool>(), It.IsAny<Currency>()))
            .ReturnsAsync((decimal price, bool _, Currency currency) => $"{currency.CurrencyCode} {price.ToString("0.00", CultureInfo.InvariantCulture)}");
        WorkContext = new Mock<IWorkContext>();
        WorkContext.Setup(w => w.GetWorkingLanguageAsync()).ReturnsAsync(new Language { Id = 1, LanguageCulture = "en-US", UniqueSeoCode = "en", Name = "English" });
        WorkContext.Setup(w => w.GetCurrentCustomerAsync()).ReturnsAsync(() => shop.Customer);
        StoreContext = new Mock<IStoreContext>();
        StoreContext.Setup(s => s.GetCurrentStoreAsync()).ReturnsAsync(new Store { Id = 1, Name = "Sunrise Bakery" });
        UrlHelper = new Mock<INopUrlHelper>();
        UrlHelper.Setup(u => u.RouteUrl(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string name, object values, string _, string _, string _) => Url(name, values));

        Store = new SattlemeStore(shop.Database.NewProvider());
        Keys = new IntegrationKeys(Encryption.Object, SettingService.Object);
        Notes = new OrderNotes(LanguageService.Object, Localization.Object, OrderService.Object, new LocalizationSettings { DefaultAdminLanguageId = 1 }, shop.Time);
        Maker = new InvoiceMaker(new CurrencySettings { PrimaryStoreCurrencyId = shop.PrimaryCurrency.Id }, CurrencyService.Object, Logger.Object, Keys,
            shop.Gateway, SettingService.Object, StoreService.Object, Notes, Store, shop.Time);
        Shredder = new PersonalDataShredder(Store);
        Checker = new PaymentChecker(Logger.Object, Keys, Maker, OrderProcessing.Object, OrderService.Object, shop.Gateway, Notes, Shredder, Store, shop.Time);
        Task = new SattlemeCheckTask(Logger.Object, OrderService.Object, Notes, Checker, Shredder, Store, shop.Time);
        Factory = new PaymentModelFactory(CurrencyService.Object, DateTimeHelper.Object, Localization.Object, UrlHelper.Object, PriceFormatter.Object, WorkContext.Object, shop.Time);
        Messages = new CustomerMessages(DateTimeHelper.Object, Localization.Object);
    }

    /// <summary>The shop.</summary>
    public Shop Shop { get; }

    /// <summary>nopCommerce's services, faked.</summary>
    public Mock<IOrderService> OrderService { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IOrderProcessingService> OrderProcessing { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<ILogger> Logger { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<ISettingService> SettingService { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<ILocalizationService> Localization { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IEncryptionService> Encryption { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<ICurrencyService> CurrencyService { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IStoreService> StoreService { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<ILanguageService> LanguageService { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IDateTimeHelper> DateTimeHelper { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IPriceFormatter> PriceFormatter { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IWorkContext> WorkContext { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<IStoreContext> StoreContext { get; }

    /// <inheritdoc cref="OrderService"/>
    public Mock<INopUrlHelper> UrlHelper { get; }

    /// <summary>The plugin's services.</summary>
    public SattlemeStore Store { get; }

    /// <inheritdoc cref="Store"/>
    public IntegrationKeys Keys { get; }

    /// <inheritdoc cref="Store"/>
    public OrderNotes Notes { get; }

    /// <inheritdoc cref="Store"/>
    public InvoiceMaker Maker { get; }

    /// <inheritdoc cref="Store"/>
    public PersonalDataShredder Shredder { get; }

    /// <inheritdoc cref="Store"/>
    public PaymentChecker Checker { get; }

    /// <inheritdoc cref="Store"/>
    public SattlemeCheckTask Task { get; }

    /// <inheritdoc cref="Store"/>
    public PaymentModelFactory Factory { get; }

    /// <inheritdoc cref="Store"/>
    public CustomerMessages Messages { get; }

    /// <summary>A route's URL as the fake URL helper writes it: /name?key=value&amp;…</summary>
    public static string Url(string name, object? values)
    {
        var query = values is null
            ? ""
            : string.Join("&", values.GetType().GetProperties().Select(property => $"{property.Name}={property.GetValue(values)}"));
        return $"/{name}?{query}";
    }
}

/// <summary>nopCommerce's encryption, faked: reversible, keyed, and refusing what another key encrypted.</summary>
public static class Encryption
{
    /// <summary>The store's encryption key.</summary>
    public const string STORE_KEY = "store-key-0000000";

    /// <summary>Encrypts <paramref name="text"/> with <paramref name="key"/> (the store's when empty).</summary>
    public static string Encrypt(string text, string key = "")
    {
        var used = string.IsNullOrEmpty(key) ? STORE_KEY : key;
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(used + "|" + text));
    }

    /// <summary>Decrypts <paramref name="text"/> with <paramref name="key"/>: throws when it was encrypted with another one.</summary>
    public static string Decrypt(string text, string key = "")
    {
        var used = string.IsNullOrEmpty(key) ? STORE_KEY : key;
        var plain = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(text));
        return plain.StartsWith(used + "|", StringComparison.Ordinal)
            ? plain[(used.Length + 1)..]
            : throw new System.Security.Cryptography.CryptographicException("Bad data");
    }
}

/// <summary>The plugin's texts of one language, read from its language pack as nopCommerce imports it.</summary>
public sealed class Texts
{
    private readonly Dictionary<string, string> _values;

    private Texts(Dictionary<string, string> values)
    {
        _values = values;
    }

    /// <summary>The names and values.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>The pack <paramref name="language"/> (en, fr, de, it) of the plugin's sources.</summary>
    public static Texts Of(string language)
    {
        var document = XDocument.Load(Path.Combine(Repository.Plugin, "Localization", language + ".xml"));
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in document.Root!.Elements("LocaleResource"))
            values[(string)resource.Attribute("Name")!] = (string)resource.Element("Value")!;
        return new Texts(values);
    }

    /// <summary>The value of <paramref name="name"/>, or null.</summary>
    public string? Get(string name)
    {
        return _values.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The plugin's resource <paramref name="name"/> (after its prefix), filled in.</summary>
    public string Format(string name, params object[] arguments)
    {
        return string.Format(CultureInfo.GetCultureInfo("en-US"), _values[SattlemeDefaults.RESOURCE_PREFIX + name], arguments);
    }
}