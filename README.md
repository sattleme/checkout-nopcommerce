# sattle.me for nopCommerce

A payment plugin for [nopCommerce](https://www.nopcommerce.com): customers pay their orders in
bitcoin, in Lightning or on-chain, and the bitcoin goes straight to the merchant's
[sattle.me](https://sattle.me) wallet. It is built on the sattle.me checkout SDK for .NET,
`Sattleme.Checkout` ([source](https://github.com/sattleme/checkout-dotnet)), which makes and checks
the invoices: no sattle.me server, no API key of a third party, and the integration key can make
invoices that pay the merchant, never spend the funds.

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [Payments and orders](#payments-and-orders)
- [Background checks](#background-checks)
- [Security](#security)
- [Privacy](#privacy)
- [Known limits](#known-limits)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [Releases](#releases)
- [License](#license)

## How it works

1. At checkout, the customer chooses **Bitcoin** (or the name the merchant gives the method). When
   they confirm the order, the plugin asks the SDK for its invoice (nopCommerce's
   `ProcessPaymentAsync`, before nopCommerce stores the order): a Lightning invoice, a deposit
   address of the merchant's wallet, or both, depending on the amount (see
   [the SDK](https://github.com/sattleme/checkout-dotnet#on-chain-payments)). When none can be made,
   the order is not placed: the customer is told why, and may choose another payment method.
2. nopCommerce then sends the customer to the plugin's payment page, in the shop's design and
   language (a redirection payment method: `PostProcessPaymentAsync` redirects, as nopCommerce's
   documentation describes them). It shows one QR code of the invoice (BIP-21 with `lightning=`,
   or `lightning:`), a button that opens the customer's wallet with it, the Lightning invoice and
   the address to copy, the amount (in bitcoin, in sats and in the order's currency), and the time
   left to pay; the rest of an underpayment, and "being confirmed" while an on-chain payment
   waits for its confirmations.
3. The page follows the payment on its own. Once the SDK reports it **paid**, the plugin marks the
   order paid through nopCommerce (`MarkOrderAsPaidAsync`), which then goes on as for any paid
   order (its status, its emails, its downloads).

The page is rendered on the server (the QR code is an inline SVG drawn from numbers); its small
script, a file of the plugin, only counts down, copies, and reloads the page when the payment
moved on. Without JavaScript, the page offers a button to check the payment. The order's page
(My account → Orders) says where its payment is and links to the payment page; in the admin area,
the order's page has a block with its invoices.

## Requirements

- **nopCommerce 5.00** (`plugin.json`: `SupportedVersions` `5.00`), released on 2026-10-27;
  5.00.1, its latest release, on 2026-11-09 (from the tags of
  [nopSolutions/nopCommerce](https://github.com/nopSolutions/nopCommerce) and its images on Docker
  Hub, read on 2026-11-22). The plugin targets nopCommerce 5.00's framework, **.NET 10**
  (`net10.0`), and is built and tested against nopCommerce 5.00.1 (commit
  `daed7a6ab14badc5920dac1cbc2b2c77dfc1066c`). nopCommerce 4.90 runs on .NET 9, whose support
  ended on 2026-11-10: this version of the plugin does not support it.
- nopCommerce's database, any it supports (SQL Server, MySQL, PostgreSQL): the plugin's two
  tables are made by its migration, with nopCommerce's own column types. The end-to-end test runs
  on PostgreSQL; the unit tests on SQLite.
- nopCommerce's **scheduled tasks** running, as they do in any installation: nopCommerce's
  scheduler posts to the store's URL (Admin → Configuration → Stores), which the server must reach
  ([Background checks](#background-checks)).
- A merchant account of sattle.me, and an **integration** created in its app.
- **Outbound connections** from the nopCommerce server (never from the customer's browser):
  - the merchant's **Nostr relays**, over WebSocket (TLS): those the integration key names, or
    the merchant's app;
  - the merchant's **Lightning address** (breez.tips for a sattle.me account) and the URLs its
    provider gives (its callback, its verify URLs), over HTTPS;
  - the **rate sources**, one after the other until one answers: CoinGecko, mempool.space,
    Coinbase, Kraken, OpenNode;
  - for an invoice with a deposit address, the **block explorer**: **mempool.space**, or the
    merchant's own **Bitcoin node** when the merchant names one in the sattle.me app (the server
    then reads the chain from it, over HTTPS, or plain HTTP to an address of a local network, with
    mempool.space as its fallback unless the merchant chose none); and **api.blockcypher.com** for
    a transaction no check saw in time. A node on the merchant's own network answers only a server
    on that network: from any other, its fallback applies.

  Every request, and the handshake of each WebSocket, says `User-Agent: sattleme-Checkout/1`, and
  goes through the server's system proxy when it has one (.NET's default). See the SDK's
  [Outbound requests](https://github.com/sattleme/checkout-dotnet#outbound-requests).

## Installation

The plugin is distributed on nopCommerce's marketplace, as an archive
(`Payments.Sattleme-<version>.zip`: one folder `Payments.Sattleme` with its `plugin.json`, and
beside it `uploadedItems.json`, nopCommerce's list of what an archive holds, which its marketplace
asks of every package):

1. **Admin → Configuration → Local plugins → Upload plugin or theme**, and choose the archive.
2. Find **Bitcoin (sattle.me)** in the list (group "Payment methods"), and click **Install**.
3. **Restart application to apply changes** (at the top of the list). nopCommerce installs the
   plugin as it starts again: its tables, its settings, its texts in each of the store's languages,
   its scheduled task, and its blocks on the order's pages.

Or copy the archive's folder into nopCommerce's `Plugins/` directory, and install it from the list
as above. To update the plugin, upload the new archive, and restart.

**Uninstalling** the plugin deletes its tables, that is its invoices and their checks: it refuses
while a payment may still come (an invoice that can still be paid, or a payment being confirmed),
and says how many.

## Configuration

1. In the sattle.me app, create an integration for your shop and copy its secret key
   (`sattleme_ck_…`).
2. In nopCommerce, **Admin → Configuration → Payment methods → Bitcoin (sattle.me) → Configure**
   (who may manage payment methods sees the page).
3. Enter the **integration key**. It is checked when you save it: the SDK refuses what is not an
   integration key, then reads the merchant of the integration on its relays, so that the key of
   an integration removed in the app, or of one the relays do not know, is refused. Once saved,
   it is never shown again: the page says whether a key is saved, and a new one replaces it. In a
   store's own configuration (nopCommerce's multi-store scope), a store may have a key of its own;
   **Remove the key** removes the key of the scope shown.
4. **Enabled**: the method is offered at checkout (nopCommerce's list of active payment methods,
   the same as **Is active** in the list of payment methods). It needs a key.
5. **Time to pay**: how long the customer has to pay an invoice, 1 to 60 minutes (15 by default).
6. Optionally, the **name** of the method and its **description** at checkout, in each of the
   store's languages (plain text). Without them, the method is called "Bitcoin", and the checkout
   says how a Bitcoin payment goes.

nopCommerce's own settings of a payment method apply as well: an additional fee (fixed or a
percentage), the countries it is not offered in (**Payment restrictions**), the stores and the
customer roles the plugin is limited to (Local plugins → Edit).

The times the plugin's pages show (until when an invoice can be paid, its checks) are in
nopCommerce's time zone: the customer's own when customers may choose one, else the store's
default time zone (Admin → Configuration → Settings → Customer settings, **Default store time
zone**), which is the server's when left empty, often UTC. The order's notes write times in UTC.

The method is offered only when the store's primary currency is one the SDK converts (its table of
currencies); the configuration page warns otherwise. Prices are charged in that currency, as
nopCommerce's own payment methods charge them.

Change the key only while no Bitcoin payment is pending: each check uses the key set for the
order's store at its time, and an invoice with a deposit address made under another integration's
key would be judged without its records in the merchant's registry (its lease, its tolerance, the
invoices of its rest).

**Test mode.** sattle.me has no test network: payments are real bitcoin payments, to the
merchant's wallet.

## Payments and orders

| What the SDK reports                     | What the plugin does                                                                  |
| ---------------------------------------- | ------------------------------------------------------------------------------------- |
| An invoice was made                      | The order is placed, its payment **pending**; the customer goes to the payment page.  |
| `open`                                   | Waits. A new invoice only once a check after its end finds it expired.                 |
| `open` with `received` (on-chain, short) | The page shows what came and a QR code for the rest (the SDK asks for it).             |
| `unconfirmed`                            | Waits for 3 confirmations (about 30 minutes); a note on the order.                     |
| `paid` (Lightning, on-chain, or both)    | Marks the order **paid**, its capture transaction the invoice's reference; a note.     |
| `paid`, the order owing another amount   | Leaves the payment **pending**, with a note of what came and what the order owes now.  |
| `paid` with `received` other than `sats` | Notes by how much: short (the merchant's tolerance or acceptance), or an overpayment.  |
| `paid`, the order cancelled meanwhile    | Leaves the order cancelled, with a note: the bitcoin came, to refund or settle.        |
| `expired`                                | Notes it: the customer can get a new invoice on the payment page.                      |
| `expired` with `received` above 0        | Notes what reached the wallet, to settle with the customer; no new invoice.            |

`expired` is final as soon as the SDK reports it, even for an underpayment the merchant accepted in
the app but the check read late: the merchant settles that order by hand
([Known limits](#known-limits)).

- **Only a `paid` status completes an order**, from the SDK's own check of the invoice the order
  stored, on the server: never because the customer's browser says so. The order is marked paid
  once, through nopCommerce's `CanMarkOrderAsPaid` and `MarkOrderAsPaidAsync`, whichever check
  sees the payment first (the page's or the background's): each check holds the invoice alone
  (a conditional update of its record), and so does the completion.
- **One try, one reference.** Each invoice is asked for a random reference (`nop-` and 128 bits),
  stored before the SDK is asked, never used for another try nor taken from the order's number:
  no deleted order, restored backup or other installation can share it. A try that follows a
  failed one (a timeout) asks for the same reference: the SDK then hands out the invoice it may
  have made meanwhile (`InvoicePendingException`), never a second one, or reports it paid
  (`OrderPaidException`), and the order is completed by the next check.
- **The order locked.** The invoice is made with the order locked in the database (a conditional
  update of the plugin's record of the order, on every database nopCommerce runs on): two requests
  at once (a double click, two servers) get one invoice.
- **One payable invoice per order.** A Lightning invoice cannot be revoked: until the order's
  invoice is known to have ended unpaid, the customer cannot get another one (the page says until
  when), so that the order is never paid twice. Known from its final status only, `expired` with
  nothing received, which a check after its end (its 120 seconds of grace included) gives: its
  time being over is not enough, a payment of its last seconds or an on-chain one dated in time is
  found by that check. nopCommerce does not let the customer change the payment method of a placed
  order: once the invoice expired, the page offers a new one (the order's "Retry payment" leads to
  it too). "Retry payment" is offered only while the payment page lets the customer pay (an
  invoice to pay, its final status being checked, or a new invoice to get): not while a payment
  is being confirmed, once one came, nor once an invoice ended with something received, for which
  no new invoice is made (the order's page then says what came, as the payment page does).
- **An order changed after its invoice.** nopCommerce's admin can edit an order's totals and items
  in any state (Admin → Orders → Edit: `OrderController.EditOrderTotals`, `Areas/Admin/Controllers/
  OrderController.cs:1151-1177` in 5.00.1). When an invoice is found paid, the plugin compares its
  price with what the order owes then, in the store's primary currency: its total (which
  nopCommerce computes net of its gift cards and reward points: `OrderTotalCalculationService.cs:
  1392-1411`), less what was refunded. When they differ, the order is never completed
  automatically. nopCommerce has no "on hold" state (its order statuses are Pending, Processing,
  Complete and Cancelled: `OrderStatus.cs`): the payment stays **pending**, which nopCommerce's
  admin lists as unpaid, the order's notes say what came and what the order owes now, and the
  merchant settles the difference with the customer, then marks the order paid by hand. The
  customer's pages say the payment arrived and the merchant checks the order.
- **Deadlines.** nopCommerce keeps an unpaid order, and its stock reserved, until it is cancelled.
  nopCommerce 5.00 can cancel unpaid orders on its own (Admin → Configuration → Settings → Order
  settings, "Enable auto-canceling of unpaid orders": off by default, after 48 hours by default;
  its scheduled task `AutoCancelOrdersTask` runs every hour): it would also cancel an order whose
  Bitcoin payment is still pending then (an on-chain payment being confirmed, an underpayment, a
  payment of another amount than the order's). A payment that comes all the same is noted on the
  cancelled order, never lost, and the merchant refunds or settles it; to keep it from happening,
  add **Bitcoin (sattle.me)** to that setting's "Excluded payment methods". The customer may
  cancel an order pending payment (nopCommerce's "Cancel order", on by default: `OrderSettings.
  AllowCustomersCancelOrders`): the plugin keeps them from it while a payment may still come (its
  invoice can still be paid, or an on-chain payment is being confirmed), or once something came
  (paid, or an underpayment that ended in the merchant's wallet, which the merchant settles): the
  order's page then offers no "Cancel order" (the plugin changes the page's model through
  nopCommerce's `ModelPreparedEvent`), and the cancellation is refused all the same if asked. The
  admin may cancel any order: its page warns while a payment may still come, and a payment that
  comes all the same is noted on the cancelled order, never lost.
- **Underpayments.** When less than the amount comes on-chain in time, the page shows the rest,
  as a Lightning invoice the SDK asks for. The merchant may accept an on-chain payment a little
  short of the amount (its tolerance, set in the sattle.me app): the SDK then reports it `paid`,
  and the order's notes say how much came. So it does when the merchant accepts by hand, in the
  sattle.me app, an order underpaid on-chain at what it received (the merchant's acceptance): being
  confirmed, then `paid` with `received` below `sats`, and the order is marked paid.
- **Refunds** are not automatic: a Lightning payment can only be refunded to an invoice of the
  customer's, an on-chain one to an address of theirs, which nopCommerce never asks. Refund the
  customer from the wallet, then record it in nopCommerce (**Refund (offline)** on the order's
  page).
- The admin's order page shows, for each invoice of the order: its reference, its state, the amount
  in sats and BTC and the price it was converted from, the exchange rate (its source and time), what
  came (once paid, how: in Lightning, its Lightning invoice's amount, which the SDK's status does
  not repeat; on-chain; or both), the confirmations, the deposit address and the transactions
  (linked to mempool.space), the proof of payment (preimage), when it was made, ends, was last
  checked and is checked next, and its last error in plain words. The order's notes (admin only) log
  the invoices made (each one's reference, sats, end in UTC, and whether it is payable in Lightning,
  on-chain or both; the first one once nopCommerce placed the order), the payments being confirmed
  and paid, the underpayments, the payments left to the merchant, those of another amount than the
  invoice's, and why an invoice could not be made. A note of a payment says what came against the
  invoice's amount, and how: in Lightning (its Lightning invoice's amount), on-chain (in how many
  transactions) or both (the rest of an underpayment paid in Lightning: what came in all, the SDK
  giving no split of it; while being confirmed, the SDK does not say whether a Lightning payment of
  the rest is part of it).

## Background checks

The SDK has no webhooks: payments are checked, by the plugin, on the nopCommerce server.

- While the customer's payment page is open: the page asks every few seconds, and the server
  checks the invoice at most every 5 seconds, whoever asks.
- In the background, nopCommerce's scheduler runs the plugin's task, **Check the Bitcoin payments
  (sattle.me)**, every minute (Admin → System → Schedule tasks). A run checks the invoices due when
  it starts. Each invoice is checked at every run while it can be paid, up to the end of its grace
  period (its end and 120 seconds: an on-chain payment a check lists by then came in time), at a
  run 10 seconds or more after that end (its final status), then at every fifth run, every five
  minutes, until its final status is known (an on-chain payment being confirmed). After failed
  checks: one minute later, then twice as long each time, up to every 30 minutes. Those waits
  count from the start of the run that made the check, less 5 seconds, so that the run a minute
  (or five) later takes it: counted from the check's end, which comes seconds after its run
  started, they would miss that run for the next (every two minutes, every six). A run checks the
  invoices that are due, the longest due first, 20 at most and for about 45 seconds (each check is
  bounded at 20 seconds; nopCommerce holds a task's lock for its period less a second:
  `ScheduleTaskRunner.cs:136-141`).

An invoice whose final status is still unknown after 90 days (an on-chain payment made in time,
still unconfirmed on a congested network) is no longer checked; a note says so on its order, whose
payment stays pending: check it in the sattle.me app and settle it by hand.

If the plugin finds a payment but nopCommerce fails to mark its order paid (its database, a plugin
reacting to the payment), the invoice stays unsettled and a later check marks the order paid.

## Security

- **The integration key** is stored in nopCommerce's settings, **encrypted** with nopCommerce's
  encryption service (`IEncryptionService`: TripleDES, or AES when the store uses it, keyed by the
  store's encryption key, Admin → Settings → General settings → Security), as nopCommerce
  encrypts its own secrets. That key is a setting of the same database: the encryption keeps the
  integration key out of plain sight (the list of all settings, an export of them), not from
  whoever reads the whole database. When the encryption key is changed, nopCommerce publishes
  `SecuritySettingsChangedEvent` (`SettingController.cs:1801`), and the plugin encrypts the saved
  keys again with the new one. The key never goes back to the browser (the field is a password
  field without a value; a key refused is not sent back either), and never goes in a log, an
  exception, a page or a note of the plugin; the SDK never puts it, or a key derived from it, in a
  message or a representation.
- **One key, one shop.** Create an integration for your nopCommerce shop, and use its key nowhere
  else. If it leaks, remove the integration in the sattle.me app and create a new one: the key can
  make invoices that pay you, never spend your funds.
- **Payment links** carry the order's GUID and a secret of the order (32 random bytes), compared
  in constant time: an order's number never opens them. Like the order's own page, a payment link
  is the customer's. The pages are never cached (`Cache-Control: no-store`), never indexed
  (`noindex` in the page and `X-Robots-Tag`), send no referrer, and use no inline script of the
  plugin's; the shop's own checks of its public pages apply (a closed store, access to the public
  store). Nothing the browser sends decides an amount, a currency, a reference or a status: the
  page and its status show what the server's own checks found.
- **Forms.** Every change posts nopCommerce's antiforgery token (the configuration, the new
  invoice); the configuration page is the admin area's, for who may manage payment methods. What
  the pages show is encoded (the plugin's views write its texts as text, never nopCommerce's
  resources as HTML; the order's notes are text, which nopCommerce encodes).
- **Outbound requests.** The SDK connects to hosts the merchant chooses (its relays, its Lightning
  address, its Bitcoin node). It refuses IP addresses and `localhost`, follows no redirects and
  checks TLS, but a name may still lead to an address of your own network: if your nopCommerce
  server sits in a network with services of its own, restrict what it can reach to public
  addresses (see the SDK's
  [Outbound requests](https://github.com/sattleme/checkout-dotnet#outbound-requests)). The
  merchant's Bitcoin node has a rule of its own: plain HTTP and an IPv4 address are allowed for a
  host of a local network (10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, or a name
  under `.local`), never `localhost`; the server sends it GET requests at mempool.space's paths,
  about deposit addresses only, and trusts what it shows as it trusts mempool.space. A server whose
  outbound connections are restricted to public addresses cannot reach such a node: its fallback
  then applies.
- **The SDK in the plugin.** nopCommerce loads every plugin's assemblies into one context, the
  application's: each with `Assembly.LoadFrom`
  (`Nop.Web.Framework/Infrastructure/Extensions/ApplicationPartManagerExtensions.cs:122` in
  5.00.1), and an assembly of a plugin whose file name is already loaded (by nopCommerce or by
  another plugin) is not loaded again (`IsAlreadyLoaded`, lines 184-227, used at 280-285): the
  first one loaded serves both, and a plugin whose version of it differs is recorded as colliding
  (`PluginLoadedAssemblyInfo.cs:44-45`) and marked incompatible, its plugin left out
  (`CheckCompatible`, lines 88-104, called at 309). Another plugin of the same shop may carry its
  own Bitcoin libraries: BTCPay Server's nopCommerce plugin
  ([btcpayserver/nopcommerce](https://github.com/btcpayserver/nopcommerce), commit `21afcef`)
  references `BTCPayServer.Client` 2.0.1 with `CopyLocalLockFileAssemblies`, so its folder holds
  `BTCPayServer.Client.dll`, `BTCPayServer.Lightning.Common.dll` 1.5.1 and `NBitcoin.dll` 7.0.45
  (nopCommerce's ClearPluginAssemblies removes `Newtonsoft.Json.dll`, which nopCommerce has); and
  nopCommerce's own Google Authenticator plugin ships `QRCoder.dll`. So the plugin ships neither
  `Sattleme.Checkout.dll` nor `NBitcoin.Secp256k1.dll`, nor a QR code library of a common name:
  the SDK's sources (`sdk/`), `NBitcoin.Secp256k1`'s at the exact version the SDK pins (4.0.4, its
  repository's tag `NBitcoin.Secp256k1/v4.0.4`) and the QR code generator's
  (`Net.Codecrete.QrCodeGenerator` 3.2.1, tag `v3.2.1`), all MIT and pinned in `sdk/` and
  `vendor/` unchanged, are compiled into three assemblies of the plugin's own names
  (`Nop.Plugin.Payments.Sattleme.Sdk`, `.Secp256k1`, `.QrCode`), their namespaces prefixed with
  `NopSattleme.` (`src/Embedded/Embedded.targets`). It is the simplest reliable way: the SDK
  reads nothing of its own name at run time (no reflection, no embedded resource: its README, "In
  a platform's plugin"), so it builds and runs renamed as it is, with no tool to maintain beside
  the compiler (a merging tool such as ILRepack rewrites the compiled code, and would then need its
  own checks). The tests check the archive's assemblies (their names, their references, every type
  under the prefix), that they are byte for byte the ones tested, and run the SDK's own vectors
  for integration keys, BIP-340 signatures and NIP-44 against them.
- **Themes.** nopCommerce 5.00 lets the store's theme replace a plugin's public views
  (`Themes/<theme>/Plugins/Payments.Sattleme/Views/…`, `ThemeableRazorViewEngine`): a theme that
  replaces the payment page's views is the shop's own code, which the plugin's tests do not check.
- Report a vulnerability privately: see [SECURITY.md](SECURITY.md).

## Privacy

- **What the plugin stores**, in nopCommerce's database (its tables `SattlemeOrder` and
  `SattlemeInvoice`): for each order paid with Bitcoin, the secret of its payment page; for each
  invoice, its reference, the price it was asked for, the invoice as the SDK made it (amounts in
  sats and in the store's currency, exchange rate, Lightning invoice and, from 1,000 sats, a
  deposit address of the merchant's wallet) and what its checks found (its state, what came, the
  confirmations, the transactions, the proof of payment). No name, no email, no IP address: the
  customer's are nopCommerce's own.
- **What the nopCommerce server sends**, and to whom (the SDK's
  [What `CreateInvoiceAsync` does](https://github.com/sattleme/checkout-dotnet#what-createinvoiceasync-does)):
  - the merchant's **Nostr relays**, encrypted for the merchant (NIP-44): each invoice, with its
    price and its note (the store's name and the first 8 characters of the order's GUID), which
    the merchant's app shows;
  - the merchant's **Lightning address** (breez.tips for a sattle.me account): asked for each
    invoice, its amount and, when the address accepts comments, its note, then for its payment;
  - the **rate sources**, one at a time until one answers: asked for the price of bitcoin;
  - the **block explorer**, for an invoice with an address: mempool.space, or the merchant's
    Bitcoin node when the merchant names one (and mempool.space too when the node does not answer
    and its fallback is mempool.space): the address and its transactions;
    **api.blockcypher.com**: a transaction, only for one no check saw in time.

  Each of them sees the nopCommerce server's IP address, never the customer's: the customer's
  browser talks to the shop only (the payment page loads nothing from elsewhere).
- **Removing personal data.** When nopCommerce removes a customer's personal data (Admin →
  Customers → GDPR, **Delete**: `GdprService.PermanentDeleteCustomerAsync`, which keeps their
  orders and publishes `CustomerPermanentlyDeleted`, `GdprService.cs:351`), the plugin removes
  what leads to the customer's coins on the blockchain from the invoices of their orders: the
  deposit address (and the payment URI that holds it), the transactions, and the rest's QR code.
  Their references, amounts, exchange rates, dates and states stay, for the merchant's accounting;
  so do their Lightning invoices, which name the merchant's node, never the customer's, and the
  proofs of payment. An invoice still being checked then (an on-chain payment being confirmed)
  loses them once final. The order's notes never hold an address nor a transaction. nopCommerce's
  GDPR export (`ExportManager.ExportCustomerGdprInfoToXlsxAsync`, `ExportManager.cs:2817`) offers
  plugins no hook: it holds no Bitcoin data, whose only link to the customer's coins is what the
  deletion above removes. What the merchant's relays received stays theirs: the merchant's app
  keeps its sales.

## Known limits

- **nopCommerce 5.00 only**: nopCommerce checks a plugin's `SupportedVersions` against its
  `CURRENT_VERSION` (`PluginsInfo.cs:210` in 5.00.1): nopCommerce 4.90 marks this version
  incompatible and leaves it out (`PluginsInfo.cs:208` in 4.90.8), and its .NET 9 could not load
  the plugin's .NET 10 assemblies anyway.
- **nopCommerce's reminders of pending orders.** nopCommerce 5.00 emails a customer whose order's
  payment is still pending ("You haven't completed the order", 3 days after the order by default,
  then a second one; Admin → Promotions → Reminders: on in a new installation, off in one upgraded
  from 4.90). It does not know the plugin's states: a customer whose on-chain payment is still
  being confirmed, whose underpayment the merchant settles, or whose order changed after its
  payment gets them too. Turn those reminders off, or word their message templates for it.
- **Currencies.** The store's primary currency must be one the SDK knows (its table of currencies)
  and one its rate sources price. An amount it cannot convert, or out of the merchant's limits, is
  refused when the order is placed, with a message, and the customer chooses another method.
- **One method per order.** nopCommerce does not change the payment method of a placed order: a
  customer whose invoice expired gets a new one, or cancels the order and orders again with
  another method.
- **Confirmations.** An on-chain payment is paid at 3 confirmations on the block explorer read
  (mempool.space, or the merchant's Bitcoin node), about 30 minutes: the order stays pending until
  then. What that one explorer shows decides whether the order is paid.
- **The merchant's Bitcoin node.** With its fallback set to none, while the node does not answer
  no deposit address is leased (an order payable on-chain only cannot be placed, others get a
  Lightning invoice only) and no on-chain payment is judged: the order stays pending until the
  node answers again (90 days at most, as for any invoice).
- **An underpayment accepted, read late.** `expired` is final for the plugin as soon as the SDK
  reports it. In one rare case the specification names
  ([SPEC.md §3](https://github.com/sattleme/checkout-spec/blob/1bd7966d0cff77c2538c00e8929b65f0796d9220/SPEC.md#3-data-model)),
  the merchant accepted an underpaid order in the sattle.me app, but a relay holding that
  acceptance failed at the check after the invoice's end: the SDK then reports `expired`, and the
  shop shows the order as ended underpaid, its payment pending (or the order cancelled), although
  the merchant accepted it. The plugin does not check that invoice again: the merchant settles the
  order by hand (marks it paid).
- **Time.** Making an invoice runs in the request that places the order, bounded at 25 seconds;
  a check, in the request of the payment page or in the scheduled task, at 20 seconds.
- **The scheduler** must reach the store's URL from the server: behind a proxy that refuses it, or
  with a wrong store URL, the background checks do not run (nopCommerce's log says so); the page's
  checks still do, while it is open.
- **Deposit addresses.** Each invoice of 1,000 sats or more may lease an address of the
  integration's lot for its time: many orders left unpaid at once can empty it, the next ones are
  then payable in Lightning only until the merchant opens the app (see the SDK).
- **Two orders of one GUID.** nopCommerce reuses an order's GUID for a customer's new tries within
  3 minutes (`PaymentSettings.RegenerateOrderGuidInterval`): a try whose total changed meanwhile is
  refused until the first invoice ends, or the GUID is renewed.

## Troubleshooting

The admin's order page (the Bitcoin payment block) and the order's notes say what went wrong, in
plain words; nopCommerce's log (Admin → System → Log, messages starting with `sattle.me:`) says it
too, without the key.

- **The key is refused when saving the settings.**
  - "This is not the secret key of a sattle.me integration": copy the whole key from the app,
    `sattleme_ck_` included.
  - "This integration was removed in the sattle.me app": create a new integration, and enter its
    key.
  - "This integration could not be found": the merchant's relays do not hold it (yet): open the
    sattle.me app, check the key, and try again.
  - "This integration needs a newer version of the sattle.me plugin": update the plugin.
  - "This key could not be verified": the server could not reach the sattle.me service. Check its
    outbound connections ([Requirements](#requirements)).
- **The method is not offered at checkout**: no key saved for the store (or one that cannot be
  read: "the store's encryption key changed": enter the key again), the method not enabled, or a
  primary currency the SDK does not convert (the configuration page warns).
- **"Bitcoin payments are not available right now"**: nopCommerce's log says why: an integration
  key no longer valid or removed, the integration not found on its relays, the merchant's Lightning
  address answering wrongly, or a clock that is off (keep the server's synchronized).
- **"The Bitcoin invoice could not be made right now"**: the relays, the Lightning address or the
  rate sources did not answer in time, or the amount can only be paid on-chain and no deposit
  address could be leased: none of the integration's was free (the merchant opens the app to give
  it more), or the merchant's Bitcoin node did not answer and its fallback is none. The customer
  tries again; if it goes on, check the server's outbound connections (and that it can reach the
  merchant's node, when the merchant set one).
- **"This order cannot be paid with Bitcoin"**: the SDK refused the amount or the currency
  ([Known limits](#known-limits)).
- **A payment stays pending.** The Bitcoin payment block shows its last check and its next one.
  No check at all: the scheduled task **Check the Bitcoin payments (sattle.me)** must be enabled
  and nopCommerce's scheduler must reach the store's URL. An on-chain payment waits for 3
  confirmations, about 30 minutes, longer with a low fee. When the merchant's Bitcoin node does
  not answer and its fallback is none, the checks of an invoice with an address fail (the block's
  last error) until it answers again: never judged unpaid for want of an answer. "no final status
  after 90 days, its checks stopped": check the payment in the sattle.me app, and settle the order
  by hand.
- **"… ended underpaid"** in the order's notes: less than the amount came, and reached the
  wallet: settle the difference with the customer.
- **"The order owes … now"** in the order's notes, the payment pending: the order changed after
  its invoice was made, then the invoice was paid. Settle the difference with the customer, then
  mark the order paid (or refund).
- **The plugin cannot be uninstalled**: a payment may still come for some orders; wait until their
  invoices are settled (or check them in the sattle.me app).

## Development

The checks need the .NET SDK of `global.json` (10.0.404) and git; the end-to-end test, Docker and
Python 3, and Google Chrome for its screenshots.

```shell
scripts/check.sh   # every check, as the CI runs them
e2e/run.sh         # the end-to-end test, with Docker
```

- **nopCommerce's source.** The plugin is built as nopCommerce's documentation shows for a plugin
  ("How to write a plugin for nopCommerce 5.00": a project referencing `Nop.Web.Framework`), against
  nopCommerce's source at the commit of its tag `release-5.00.1`, which `scripts/nopcommerce.sh
  [<dir>]` fetches (shallow, partial and sparse: its libraries and `Nop.Web.Framework`, about
  15 MB) into `$NOPCOMMERCE_DIR` or the ignored `.nopcommerce/`; it is never kept in this
  repository. nopCommerce's assemblies are referenced, never copied into the plugin.
- **The checks**, in `scripts/check.sh`: the pinned copies (`dotnet run --project
  tools/Sattleme.Nop.Pins -c Release -- check`), the packages from the lockfiles (`dotnet restore
  Nop.Plugin.Payments.Sattleme.sln --locked-mode`), the build in Release (nopCommerce's coding
  standard and .NET's analyzers at `latest-recommended`, nullable references, warnings as errors),
  which also makes the archive (`artifacts/Release/Payments.Sattleme-1.0.1.zip`), the coding
  standard (`dotnet format --verify-no-changes`, nopCommerce's `.editorconfig` rules), the tests,
  what the archive holds, and the brand (`scripts/brand.sh`).
- **The tests** (NUnit, as nopCommerce's own; `tests/Nop.Plugin.Payments.Sattleme.Tests`) run
  against nopCommerce's assemblies, its services faked where the plugin calls them, the plugin's
  tables on SQLite through linq2db, each request on a connection of its own. They never reach the
  network: the plugin makes every client of the SDK in one place, `ISattlemeGateway`, which the
  tests replace with a fake once the real SDK has checked the key. They cover every status the SDK
  reports and its errors, the lock, the completion done once, the schedule, the pages, the
  settings, the translations, the archive, and the SDK's own vectors against the archive's
  assemblies. nopCommerce compiles a plugin's views when the shop runs (Razor's runtime
  compilation): the tests compile the archive's views the same way, with the same Razor engine
  and C# compiler and the shop's assemblies.
- **The end-to-end test**, with Docker: `e2e/run.sh` builds the archives, runs nopCommerce's
  official image (`nopcommerceteam/nopcommerce:5.00.1`, pinned by its digest) on PostgreSQL
  (`postgres:17.11-alpine`, pinned by its digest), in containers and a network of its own, removed
  afterwards; `e2e/drive.py` then installs nopCommerce with its sample data, uploads and installs
  the plugin from its archive in the admin area, enters a key, orders a product with the method
  (cart, one-page checkout), checks the payment page (its QR code, wallet link, invoice, address,
  headers, script, its secret) and the order's page of the customer, has the invoice paid, and sees
  the order paid: once through the page's status, once through the scheduled task alone, with its
  notes and the admin's block; then, with a minute to pay, an order whose invoice ends unpaid
  ("Cancel order" and "Retry payment" offered, a new invoice made) and whose new invoice ends
  underpaid (neither offered any more, the cancellation refused, what came on its pages and in its
  notes); then uninstalls both plugins, after which no table of the plugin may be left, and
  nopCommerce's log must hold no error of the plugin. The SDK is replaced there by a fake
  (`e2e/FakeSdk`, a test-only plugin installed beside it, never shipped: the plugin has no switch
  for it). `E2E_KEEP=1 e2e/run.sh` keeps the containers of a failed run, to look at them.
  `E2E_SCREENSHOTS=<dir> e2e/run.sh` also keeps, in `<dir>`, screenshots of the plugin's pages as
  the test goes (the payment page of an invoice to pay, the configuration page with its key saved,
  and the admin's block of the paid order), the pictures of the marketplace's listing: Google
  Chrome, headless (`google-chrome`, or `$E2E_CHROME`), opens them with the test's session, driven
  by `e2e/drive.py` over its DevTools protocol through a pipe (Python's standard library only).
  They show the fake's invoices.
- **The CI** (`.github/workflows/ci.yml`) runs `scripts/check.sh`'s steps, and the end-to-end
  test, on GitHub-hosted runners, and keeps the archive (`Payments.Sattleme`) and the end-to-end
  test's screenshots (`screenshots`, taken with the runner's Google Chrome) as its artifacts.
- **The pinned copies.** `sdk/` holds the SDK's own files at a commit of
  [checkout-dotnet](https://github.com/sattleme/checkout-dotnet) (the project, its sources, and the
  vectors the tests run), unchanged, with `sdk/PIN.json` (the repository, the commit, the version,
  each file's SHA-256); `vendor/` holds NBitcoin.Secp256k1's and the QR code generator's the same
  way. To take a new version of the SDK, from a clone of checkout-dotnet:

  ```shell
  dotnet run --project tools/Sattleme.Nop.Pins -c Release -- import sdk <clone> [<commit>]
  ```

  (the clone's HEAD by default), which copies the files of that commit from git's objects (never
  the working tree), writes `sdk/PIN.json`, and checks them; then `scripts/check.sh`. A new SDK
  that pins another `NBitcoin.Secp256k1` takes it the same way (`import secp256k1 <clone> <tag>`).
- **Dependencies.** The plugin references no package: nopCommerce brings its framework, and the
  SDK, NBitcoin.Secp256k1 and the QR code generator are compiled from their pinned sources. The
  tests use NUnit 5.0.0, NUnit3TestAdapter 6.3.1, Microsoft.NET.Test.Sdk 18.12.0, Moq 4.21.0 and
  Microsoft.Data.Sqlite 11.0.0, pinned in `Directory.Packages.props` and the lockfiles, and taken
  by hand. Dependabot proposes the actions' and the .NET SDK's updates, a week after each release,
  one pull request each; not the packages': the lockfiles hold nopCommerce's packages, which a
  restore without nopCommerce's source (as Dependabot's) leaves out (`.github/dependabot.yml`).
- **The translations** are nopCommerce language packs, `Localization/en.xml` (the source),
  `fr.xml`, `de.xml` (written with ss, never ß) and `it.xml`: each language of the store gets the pack of its
  two-letter code when the plugin is installed or the language added, English otherwise. The tests
  check that the four have the same resources and placeholders, and that every resource the code
  and the views use is there.

## Releases

The plugin is distributed on nopCommerce's marketplace only. A maintainer pushes a tag `vX.Y.Z`
(the version of `Directory.Build.props` and `plugin.json`) on a commit of `main`;
[`release.yml`](.github/workflows/release.yml) runs the CI again, checks the tag, builds and tests
the archive at that commit, and keeps it as the workflow's artifact, with the source zip the
marketplace's review asks for and their SHA-256, in the `marketplace` environment once a
maintainer approves; its run keeps the CI's screenshots too, the pictures of the marketplace's
listing. The marketplace has no publishing channel to push to: the team uploads the archive there
(My extensions → Upload extension). There is no GitHub release.

## License

MIT, see [LICENSE](LICENSE). The SDK, NBitcoin.Secp256k1 and the QR code generator are MIT too
(their licenses are in the archive, `licenses/`). The sattle.me name and logo (`logo.png`, the
brand's icon, shown in nopCommerce's lists of plugins and payment methods) are not under that
license.
