# Changelog

## 1.0.1 - 2026-11-23

- The archive holds `uploadedItems.json` beside the plugin's folder: nopCommerce's list of what an
  archive holds, which its marketplace asks of every package. nopCommerce then deploys the plugin
  from the folder it names (`Payments.Sattleme/`), for the versions it supports (`5.00`), as
  `plugin.json` says; the plugin itself is unchanged.
- The end-to-end test can keep screenshots of the plugin's pages (`E2E_SCREENSHOTS`), which the CI
  keeps as its artifact `screenshots`: the pictures of the marketplace's listing.

## 1.0.0 - 2026-11-23

First version: Bitcoin payments, in Lightning or on-chain, to the merchant's sattle.me wallet, for
nopCommerce 5.00 (on .NET 10), on the sattle.me checkout SDK for .NET (`Sattleme.Checkout` 1.0.0, commit
`946eb62` of [checkout-dotnet](https://github.com/sattleme/checkout-dotnet), compiled in under the
plugin's own names).

- A redirection payment method: the invoice is made when the order is placed, then the customer
  pays it on the plugin's page in the shop (one QR code, the wallet link, the Lightning invoice and
  the address to copy, the amounts, the time left, the rest of an underpayment, the
  confirmations), which works without JavaScript and is never cached nor indexed.
- A random reference per invoice, stored before the SDK is asked; the order locked in the database
  around the invoice's making; one invoice that may be paid per order; only the SDK's `paid`
  status of the server's own check completes the order, once, through nopCommerce's
  `MarkOrderAsPaidAsync`; an order that owes another amount by then stays pending, with a note.
- Background checks by nopCommerce's scheduler, every minute while an invoice can be paid, around
  the end of its grace period, then every five minutes until its final status is known, for 90
  days at most; `expired` is final as soon as the SDK reports it.
- On-chain payments read on one block explorer: mempool.space, or the merchant's own Bitcoin node
  when the merchant names one in the sattle.me app (mempool.space as its fallback unless the
  merchant chose none).
- The integration key verified when saved, stored encrypted, never shown again; the customer kept
  from cancelling an order a payment may still come for, or something came for (its page then
  offers no "Cancel order"); the admin's view of each invoice; the GDPR deletion removing what
  leads to the customer's coins.
- English, French, German and Italian.
