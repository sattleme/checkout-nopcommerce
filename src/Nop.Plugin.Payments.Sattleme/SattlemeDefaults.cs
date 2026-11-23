namespace Nop.Plugin.Payments.Sattleme;

/// <summary>
/// The plugin's names and rules: its system name, routes, resources, and how long and how often it
/// waits (README.md, "Background checks").
/// </summary>
public static class SattlemeDefaults
{
    #region Names

    /// <summary>The payment method's system name, and its folder under Plugins/.</summary>
    public const string SYSTEM_NAME = "Payments.Sattleme";

    /// <summary>The plugin's views.</summary>
    public const string VIEWS = "~/Plugins/Payments.Sattleme/Views/";

    /// <summary>The plugin's static files (the payment page's script and style).</summary>
    public const string CONTENT = "~/Plugins/Payments.Sattleme/Content/";

    /// <summary>The folder of the plugin's translations (nopCommerce language packs, one per language).</summary>
    public const string LOCALIZATION_DIRECTORY = "Localization";

    /// <summary>The prefix of the plugin's locale resources.</summary>
    public const string RESOURCE_PREFIX = "Plugins.Payments.Sattleme.";

    /// <summary>The scheduled task of the background checks: its type, as nopCommerce stores it.</summary>
    public const string CHECK_TASK_TYPE = "Nop.Plugin.Payments.Sattleme.Services.SattlemeCheckTask, Nop.Plugin.Payments.Sattleme";

    /// <summary>The scheduled task's name in the admin area.</summary>
    public const string CHECK_TASK_NAME = "Check the Bitcoin payments (sattle.me)";

    /// <summary>The order reference of each invoice: this prefix, then 128 random bits in hex.</summary>
    public const string REFERENCE_PREFIX = "nop-";

    /// <summary>The routes of the plugin.</summary>
    public static class Route
    {
        /// <summary>The configuration page in the admin area.</summary>
        public const string CONFIGURE = "Plugin.Payments.Sattleme.Configure";

        /// <summary>The page where the customer pays.</summary>
        public const string PAY = "Plugin.Payments.Sattleme.Pay";

        /// <summary>What the payment page asks, every few seconds, for news of the payment.</summary>
        public const string STATUS = "Plugin.Payments.Sattleme.Status";

        /// <summary>A new invoice, once the last one ended unpaid.</summary>
        public const string NEW_INVOICE = "Plugin.Payments.Sattleme.NewInvoice";
    }

    #endregion

    #region Invoices

    /// <summary>How many minutes the customer has to pay an invoice: the bounds of the setting, and its default.</summary>
    public const int MIN_MINUTES = 1;

    /// <inheritdoc cref="MIN_MINUTES"/>
    public const int MAX_MINUTES = 60;

    /// <inheritdoc cref="MIN_MINUTES"/>
    public const int DEFAULT_MINUTES = 15;

    /// <summary>
    /// The SDK may report a payment made in an invoice's last seconds up to this long after its end
    /// (the specification's grace period, in seconds): an invoice may be paid until then.
    /// </summary>
    public const int GRACE_SECONDS = 120;

    /// <summary>Per relay request and per HTTP request of the SDK, connection and body included, in milliseconds.</summary>
    public const int RELAY_TIMEOUT_MS = 5_000;

    /// <inheritdoc cref="RELAY_TIMEOUT_MS"/>
    public const int HTTP_TIMEOUT_MS = 8_000;

    /// <summary>Making an invoice, in the customer's request (seconds).</summary>
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(25);

    /// <summary>Verifying a key the admin enters (seconds).</summary>
    public static readonly TimeSpan VerifyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>One check of a payment (seconds).</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A request that waited longer than this for another one making the order's invoice, which made
    /// none, makes none either: two calls to the SDK in a row could outlast the request.
    /// </summary>
    public static readonly TimeSpan LockWait = TimeSpan.FromSeconds(5);

    /// <summary>A lock or a check holds its record this much longer than its call to the SDK may take.</summary>
    public static readonly TimeSpan LeaseMargin = TimeSpan.FromSeconds(30);

    /// <summary>
    /// A completion (the order marked paid) claimed this long ago and not done stopped half-way (the
    /// process ended): the next check takes it again.
    /// </summary>
    public static readonly TimeSpan CompletionStale = TimeSpan.FromMinutes(10);

    #endregion

    #region Checks

    /// <summary>The scheduled task runs this often (seconds): nopCommerce's scheduler posts to it.</summary>
    public const int CHECK_TASK_SECONDS = 60;

    /// <summary>Between two background checks of an invoice that can still be paid.</summary>
    public static readonly TimeSpan OpenInterval = TimeSpan.FromSeconds(60);

    /// <summary>Between two background checks of an invoice past its end whose final status is not known yet.</summary>
    public static readonly TimeSpan SettlingInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The last check of an invoice's grace period is due this long before its end, and the first one
    /// after it this long after.
    /// </summary>
    public static readonly TimeSpan EndMargin = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A run of the scheduled task may start up to this much less than its period after the last
    /// one (nopCommerce's scheduler ticks every period, then posts the run to the store's URL, a
    /// request whose time to arrive varies): an invoice a run checked is due again a wait after that
    /// run's start less this, so that the run a wait later checks it (PaymentRules.NextCheck).
    /// </summary>
    public static readonly TimeSpan RunSlack = TimeSpan.FromSeconds(5);

    /// <summary>The customer's payment page has its invoice checked at most this often.</summary>
    public static readonly TimeSpan PageInterval = TimeSpan.FromSeconds(5);

    /// <summary>After failed checks, the next one waits twice as long each time, up to this.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);

    /// <summary>
    /// An invoice whose final status is still unknown this long after it was made is no longer checked
    /// (an on-chain payment unconfirmed for months): its order is then settled by hand.
    /// </summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(90);

    /// <summary>
    /// One run of the scheduled task checks invoices for this long at most: nopCommerce holds the
    /// task's lock for 59 seconds (ScheduleTaskRunner: its period, at most 300 s, less one).
    /// </summary>
    public static readonly TimeSpan TaskBudget = TimeSpan.FromSeconds(45);

    /// <summary>Invoices checked per run of the scheduled task, at most.</summary>
    public const int TASK_BATCH = 20;

    #endregion
}