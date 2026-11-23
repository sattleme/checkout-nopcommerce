"""The end-to-end test of the sattle.me payment method in a real nopCommerce (e2e/run.sh starts it).

It does what a merchant and a customer do, over HTTP, as a browser would: installs nopCommerce on
PostgreSQL, logs in as its admin, uploads the plugin's archive and the test-only fake of the SDK
(Misc.SattlemeE2e), installs both, restarts, sets an integration key, then orders a product with
the Bitcoin payment method, is sent to the payment page, checks it (QR code, wallet link, no inline
script of its own, never cached nor indexed, refused without its secret), has the fake pay the
invoice, and sees the order paid: once by the page's status, once by the background checks alone,
with the order's notes and the admin's block; then, with a minute to pay, an order whose invoice
ends unpaid (the customer may cancel it, or get a new invoice) and whose new invoice ends underpaid
(the customer may no longer cancel it nor retry its payment, and its page says what came); then
uninstalls both plugins.

With --screenshots <dir>, it also keeps screenshots of the plugin's pages, as they are at those
steps, for nopCommerce's marketplace: the payment page, the configuration page, and the admin's
block of a paid order (Screenshots, below).

Python's standard library only; run with `python3 -I`.
"""

import argparse
import base64
import fcntl
import html
import html.parser
import http.cookiejar
import json
import math
import os
import re
import select
import shutil
import struct
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

KEY = (
    "sattleme_ck_qEVejHuCZpF4XMmBSw5p7iZYxCtm20XxzT1fnJ9VB8o."
    "uPTAz1d8bbQaw2Jx22Wwrga2vI2F8allY5NrPC3Aogk.cmVsYXkuZXhhbXBsZQ"
)
ADMIN_EMAIL = "admin@example.com"
ADMIN_PASSWORD = "e2e-Admin-123!"


class Failure(Exception):
    """What the test found wrong."""


def check(condition, message):
    if not condition:
        raise Failure(message)


def step(message):
    print(f"[e2e] {message}", flush=True)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Response:
    def __init__(self, status, headers, body):
        self.status = status
        self.headers = headers
        self.body = body

    @property
    def text(self):
        return self.body.decode("utf-8", "replace")

    def json(self):
        return json.loads(self.text)

    @property
    def location(self):
        return self.headers.get("Location")


class Browser:
    """A browser of the shop: cookies, the shop's host name (so that the scheduler, inside the
    container, reaches the store's URL), no redirect followed unless asked."""

    def __init__(self, base, host):
        self.base = base.rstrip("/")
        self.host = host
        self.jar = http.cookiejar.CookieJar()
        # The shop is on this machine: no proxy.
        self.opener = urllib.request.build_opener(
            urllib.request.ProxyHandler({}),
            urllib.request.HTTPCookieProcessor(self.jar),
            NoRedirect(),
        )

    def url(self, path):
        if path.startswith("http://") or path.startswith("https://"):
            parsed = urllib.parse.urlsplit(path)
            path = parsed.path + (("?" + parsed.query) if parsed.query else "")
        return self.base + path

    def request(self, method, path, data=None, headers=None, timeout=120, follow=False):
        body = None
        # An empty Accept-Encoding, as a browser asking for no compression would send: Python's
        # http.client sends "identity" otherwise, and ASP.NET Core 9's content-encoding policy (run
        # for every request, nopCommerce having dynamic routes) then keeps only the first of the
        # actions a form button picks between (NegotiationMatcherPolicy.EvaluateCandidate: a tie
        # drops the later candidate), so Install, Save and the like would answer 404.
        all_headers = {"Host": self.host, "User-Agent": "sattleme-e2e", "Accept-Encoding": ""}
        all_headers.update(headers or {})
        if isinstance(data, (dict, list)):
            body = urllib.parse.urlencode(data, doseq=True).encode()
            all_headers.setdefault("Content-Type", "application/x-www-form-urlencoded")
        elif isinstance(data, bytes):
            body = data
        request = urllib.request.Request(self.url(path), data=body, headers=all_headers, method=method)
        try:
            with self.opener.open(request, timeout=timeout) as answer:
                response = Response(answer.status, answer.headers, answer.read())
        except urllib.error.HTTPError as error:
            response = Response(error.code, error.headers, error.read())
        if follow and response.status in (301, 302, 303, 307, 308):
            return self.request("GET", response.location, timeout=timeout, follow=True)
        return response

    def get(self, path, **options):
        return self.request("GET", path, **options)

    def post(self, path, data, **options):
        return self.request("POST", path, data=data, **options)

    def multipart(self, path, fields, file_field, file_name, content):
        boundary = uuid.uuid4().hex
        parts = []
        for name, value in fields.items():
            parts.append(f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode())
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="{file_field}"; filename="{file_name}"\r\n'
            "Content-Type: application/zip\r\n\r\n".encode() + content + b"\r\n"
        )
        parts.append(f"--{boundary}--\r\n".encode())
        return self.request("POST", path, data=b"".join(parts),
                            headers={"Content-Type": f"multipart/form-data; boundary={boundary}"}, timeout=300)


class Forms(html.parser.HTMLParser):
    """The forms of a page: their action and fields, as a browser would post them."""

    def __init__(self):
        super().__init__()
        self.forms = []
        self.current = None
        self.textarea = None
        self.select = None

    def handle_starttag(self, tag, attrs):
        attributes = dict(attrs)
        if tag == "form":
            self.current = {"action": attributes.get("action", ""), "id": attributes.get("id", ""), "fields": []}
            self.forms.append(self.current)
        elif self.current is None:
            return
        elif tag == "input":
            kind = (attributes.get("type") or "text").lower()
            name = attributes.get("name")
            if not name or kind in ("submit", "button", "file", "image"):
                return
            if kind in ("checkbox", "radio") and "checked" not in attributes:
                return
            self.current["fields"].append((name, attributes.get("value") or ("true" if kind == "checkbox" else "")))
        elif tag == "textarea" and attributes.get("name"):
            self.textarea = [attributes["name"], ""]
        elif tag == "select" and attributes.get("name"):
            self.select = [attributes["name"], None, None]
        elif tag == "option" and self.select is not None:
            value = attributes.get("value", "")
            if self.select[2] is None:
                self.select[2] = value
            if "selected" in attributes:
                self.select[1] = value

    def handle_data(self, data):
        if self.textarea is not None:
            self.textarea[1] += data

    def handle_endtag(self, tag):
        if tag == "textarea" and self.textarea is not None and self.current is not None:
            self.current["fields"].append((self.textarea[0], html.unescape(self.textarea[1])))
            self.textarea = None
        elif tag == "select" and self.select is not None and self.current is not None:
            value = self.select[1] if self.select[1] is not None else (self.select[2] or "")
            self.current["fields"].append((self.select[0], value))
            self.select = None
        elif tag == "form":
            self.current = None


def forms_of(page):
    parser = Forms()
    parser.feed(page)
    return parser.forms


def form_with(page, action_part):
    for form in forms_of(page):
        if action_part.lower() in form["action"].lower():
            return form
    raise Failure(f"no form posting to {action_part}")


def token_of(page):
    match = re.search(r'name="__RequestVerificationToken"[^>]*value="([^"]+)"', page) or re.search(
        r'value="([^"]+)"[^>]*name="__RequestVerificationToken"', page)
    check(match is not None, "no antiforgery token in the page")
    return html.unescape(match.group(1))


def wait_until(what, condition, timeout, every=3):
    deadline = time.monotonic() + timeout
    while True:
        try:
            if condition():
                return
        except (urllib.error.URLError, ConnectionError, TimeoutError, OSError):
            pass
        if time.monotonic() > deadline:
            raise Failure(f"timed out waiting for {what}")
        time.sleep(every)


def install(browser, args):
    step("waiting for nopCommerce's installer")
    wait_until("the installer", lambda: browser.get("/install").status == 200, 300)
    page = browser.get("/install").text
    step("installing nopCommerce 5.00.1 with its sample data on PostgreSQL")
    answer = browser.post("/install", {
        "__RequestVerificationToken": token_of(page),
        "AdminEmail": ADMIN_EMAIL,
        "AdminPassword": ADMIN_PASSWORD,
        "ConfirmPassword": ADMIN_PASSWORD,
        "DataProvider": "PostgreSQL",
        "ServerName": args.pg_host,
        "DatabaseName": "nopcommerce",
        "Username": "postgres",
        "Password": args.pg_password,
        "IntegratedSecurity": "false",
        "ConnectionStringRaw": "false",
        "CreateDatabaseIfNotExists": "true",
        "InstallSampleData": "true",
        "SubscribeNewsletters": "false",
        "UseCustomCollation": "false",
    }, timeout=1200)
    check(answer.status == 200 and "restartapplication" in answer.text.lower(),
          f"the installation failed: {answer.status} {re.findall(r'validation-summary-errors.*?</ul>', answer.text, re.S)[:1]}")
    step("restarting nopCommerce after its installation")
    browser.get("/install/restartapplication", timeout=60)
    wait_until("the shop", lambda: browser.get("/").status == 200, 600, every=5)


def login(browser):
    page = browser.get("/login").text
    answer = browser.post("/login", {
        "__RequestVerificationToken": token_of(page),
        "Email": ADMIN_EMAIL,
        "Password": ADMIN_PASSWORD,
        "RememberMe": "false",
    })
    check(answer.status == 302, f"the admin's login failed: {answer.status}")
    check(browser.get("/Admin", follow=True).status == 200, "the admin area is not open to the admin")


def restart(browser, ready):
    """Admin, Local plugins, "Restart application to apply changes", then waits for the shop.

    The button posts the list's form; the page it answers with restarts nopCommerce by its script
    (Areas/Admin/Views/Shared/RestartApplication.cshtml: GET Admin/Common/RestartApplication),
    which is done here, there being no script engine.
    """
    page = browser.get("/Admin/Plugin/List").text
    answer = browser.post("/Admin/Plugin/List", {"__RequestVerificationToken": token_of(page), "plugin-apply-changes": ""})
    check(answer.status == 200 and "/Admin/Common/RestartApplication" in answer.text,
          f"applying the changes answered {answer.status}")
    try:
        browser.get("/Admin/Common/RestartApplication", timeout=60)
    except OSError:
        pass  # The application stops while it answers: Docker starts it again.
    time.sleep(5)
    wait_until("the shop after its restart", ready, 600, every=5)


def plugins(browser):
    """The local plugins, as the admin's list asks for them (Plugin/ListSelect): system name -> installed."""
    page = browser.get("/Admin/Plugin/List").text
    answer = browser.post("/Admin/Plugin/ListSelect", {
        "__RequestVerificationToken": token_of(page), "Draw": "1", "Start": "0", "Length": "500",
        "SearchLoadModeId": "0", "SearchGroup": "",
    })
    check(answer.status == 200, f"the list of plugins answered {answer.status}")
    return {plugin["SystemName"]: plugin["Installed"] for plugin in answer.json()["Data"]}


def upload_and_install(browser, archives):
    for path in archives:
        page = browser.get("/Admin/Plugin/List").text
        with open(path, "rb") as archive:
            content = archive.read()
        step(f"uploading {path.rsplit('/', 1)[-1]} (Configuration, Local plugins, Upload plugin or theme)")
        answer = browser.multipart("/Admin/Plugin/UploadPluginsAndThemes", {"__RequestVerificationToken": token_of(page)},
                                   "archivefile", path.rsplit("/", 1)[-1], content)
        check(answer.status == 302, f"the upload failed: {answer.status}")
    # nopCommerce 5.00 keeps uploaded plugins in Plugins/Uploaded until it restarts.
    step("restarting nopCommerce to deploy the uploaded plugins")
    restart(browser, lambda: "Payments.Sattleme" in plugins(browser))
    for system_name in ("Payments.Sattleme", "Misc.SattlemeE2e"):
        check(plugins(browser).get(system_name) is False, f"{system_name} is not in the list of plugins, to install")
        page = browser.get("/Admin/Plugin/List").text
        step(f"installing {system_name}")
        answer = browser.post("/Admin/Plugin/List", {
            "__RequestVerificationToken": token_of(page),
            f"install-plugin-link-{system_name}": "",
        })
        check(answer.status == 302, f"installing {system_name} failed: {answer.status}")
    step("restarting nopCommerce to install them")
    restart(browser, lambda: plugins(browser).get("Payments.Sattleme") is True and plugins(browser).get("Misc.SattlemeE2e") is True)


def configure(browser, minutes=15):
    page = browser.get("/Admin/Sattleme/Configure").text
    check("sattleme" in page.lower(), "the configuration page is not the plugin's")
    # Its form posts to the plugin's route (Admin/Sattleme/Configure) or to the admin area's own
    # ({area}/{controller}/{action}), which nopCommerce's URL generation picks between in the order
    # its plugins' routes came: the same action either way.
    form = next((form for form in forms_of(page)
                 if re.fullmatch(r"/Admin/Sattleme(Admin)?/Configure", form["action"])), None)
    check(form is not None, "the configuration page has no form of the plugin's")
    fields = [(name, value) for name, value in form["fields"]
              if name not in ("IntegrationKey", "Enabled", "MinutesToPay", "RemoveKey")]
    fields += [("IntegrationKey", KEY), ("Enabled", "true"), ("Enabled", "false"), ("MinutesToPay", str(minutes))]
    step(f"saving the integration key (verified by the SDK, then encrypted), {minutes} minute(s) to pay")
    answer = browser.request("POST", form["action"], data=urllib.parse.urlencode(fields).encode(),
                             headers={"Content-Type": "application/x-www-form-urlencoded"})
    check(answer.status == 302, f"the settings were not saved: {answer.status} {answer.text[:500]}")
    page = browser.get("/Admin/Sattleme/Configure").text
    check(KEY not in page and KEY[12:40] not in page, "the configuration page shows the key")
    check("A key is saved" in page, "the configuration page does not say the key is saved")
    check(re.search(r'name="IntegrationKey"[^>]*type="password"|type="password"[^>]*name="IntegrationKey"', page) is not None,
          "the key field is not a password field")
    check(re.search(r'name="IntegrationKey"[^>]*value="[^"]+"', page) is None, "the key field has a value")


def place_order(browser, payment_text=None):
    page = browser.get("/search?q=Night+Visions").text
    match = re.search(r'data-productid="(\d+)"', page)
    check(match is not None, "the sample product Night Visions is not found")
    product = match.group(1)
    answer = browser.post(f"/addproducttocart/catalog/{product}/1/1", {"__RequestVerificationToken": token_of(page)})
    check(answer.status == 200 and answer.json().get("success"), f"the product was not added to the cart: {answer.text[:300]}")

    # The cart's Checkout button, as the customer presses it: the cart's form, its checkout
    # attributes as they are (the sample shop's gift wrapping, "No"), the terms of service ticked.
    page = browser.get("/cart").text
    cart = next((form for form in forms_of(page) if form["id"] == "shopping-cart-form"), None)
    check(cart is not None, "the cart has no form")
    answer = browser.post("/cart", cart["fields"] + [("termsofservice", "on"), ("checkout", "checkout")])
    check(answer.status == 302 and "checkout" in (answer.location or "").lower(),
          f"the cart's Checkout answered {answer.status} {answer.location}")

    page = browser.get("/onepagecheckout").text
    token = token_of(page)
    address = re.search(r'name="billing_address_id"[^>]*>\s*<option[^>]*value="(\d+)"', page)
    check(address is not None, "the admin has no billing address to choose")
    # The admin's address, for shipping too (the sample products ship).
    billing = browser.post("/checkout/OpcSaveBilling", {
        "__RequestVerificationToken": token, "billing_address_id": address.group(1), "ShipToSameAddress": "true"})
    check(billing.status == 200, f"billing failed: {billing.status}")
    sections = billing.json()
    check("error" not in sections, f"billing failed: {sections}")
    if (sections.get("update_section") or {}).get("name") == "shipping-method":
        option = re.search(r'name="shippingoption"[^>]*value="([^"]+)"', sections["update_section"]["html"])
        check(option is not None, "no shipping method to choose")
        shipping = browser.post("/checkout/OpcSaveShippingMethod", {
            "__RequestVerificationToken": token, "shippingoption": html.unescape(option.group(1))})
        check(shipping.status == 200, f"choosing the shipping method failed: {shipping.status}")
        sections = shipping.json()
        check("error" not in sections, f"choosing the shipping method failed: {sections}")
    methods = json.dumps(sections)
    check("Payments.Sattleme" in methods,
          f"the Bitcoin payment method is not offered: the next step is {sections.get('update_section', {}).get('name') or sections.get('goto_section')}")
    payment = browser.post("/checkout/OpcSavePaymentMethod", {"__RequestVerificationToken": token, "paymentmethod": "Payments.Sattleme"}).json()
    check("error" not in payment, f"choosing the method failed: {payment}")
    check("Pay in bitcoin" in json.dumps(payment) or "Once you confirm the order" in json.dumps(payment),
          "the payment information step does not describe the method")
    if payment_text is not None:
        check(payment_text in json.dumps(payment), f"the payment information step does not say {payment_text!r}")
    info = browser.post("/checkout/OpcSavePaymentInfo", {"__RequestVerificationToken": token}).json()
    check("error" not in info, f"the payment information step failed: {info}")
    confirmed = browser.post("/checkout/OpcConfirmOrder", {"__RequestVerificationToken": token}, timeout=120).json()
    check("redirect" in confirmed, f"the order was not placed: {confirmed}")
    redirected = browser.get(confirmed["redirect"])
    check(redirected.status == 302, f"no redirection to the payment page: {redirected.status}")
    location = urllib.parse.urlsplit(redirected.location).path
    check(re.match(r"^/sattleme/pay/[0-9a-f-]{36}/[0-9a-f]{64}$", location) is not None, f"not the plugin's payment page: {location}")
    return location


def check_page(browser, location):
    page = browser.get(location)
    check(page.status == 200, f"the payment page answered {page.status}")
    check("no-store" in (page.headers.get("Cache-Control") or ""), "the payment page may be cached")
    check("noindex" in (page.headers.get("X-Robots-Tag") or ""), "the payment page may be indexed")
    text = page.text
    check('<meta name="robots" content="noindex, nofollow" />' in text, "no robots meta")
    body = text[text.index('id="sattleme-pay"'):]
    body = body[:body.index("sattleme-pay-back")]
    check("<script" not in body, "the payment page has a script of its own inline")
    check(re.search(r'<script[^>]*src="[^"]*/Plugins/Payments\.Sattleme/Content/pay\.js', text) is not None, "the page's script is not the plugin's file")
    check("<svg" in body and 'role="img"' in body, "no QR code")
    check(re.search(r'href="bitcoin:bc1p[^"]*lightning=lnbc', body) is not None, "no wallet link")
    check("lnbc" in body and "bc1pmzfrwwndsqmk5yh69yjr5lfgfg4ev8c0tsc06e" in body, "the invoice or the address is not shown to copy")
    check("?check=1" in body and "<noscript>" in body, "no check without JavaScript")
    check("sats" in body and ("15:00" in body or "14:5" in body), "the amount or the time left is missing")
    script = browser.get("/Plugins/Payments.Sattleme/Content/pay.js")
    check(script.status == 200 and b"sattleme-pay" in script.body, "the page's script is not served")
    wrong = location[:-1] + ("0" if location[-1] != "0" else "1")
    check(browser.get(wrong).status == 404, "the payment page opens without its secret")
    status = browser.get(location.replace("/sattleme/pay/", "/sattleme/status/"))
    check(status.status == 200 and status.json()["phase"] == "open", f"the status is not open: {status.text}")
    order = re.search(r'href="/orderdetails/(\d+)"', text)
    check(order is not None, "no link back to the order")
    return int(order.group(1))


def pay_last_invoice(browser):
    references = browser.get("/e2e-sattleme/references").json()
    check(len(references) > 0, "the fake made no invoice")
    reference = references[-1]
    check(re.match(r"^nop-[0-9a-f]{32}$", reference) is not None, f"not a random reference: {reference}")
    check(browser.post(f"/e2e-sattleme/pay/{reference}", {}).status == 200, "the fake could not pay")
    return reference


def admin_order(browser, order_id):
    return browser.get(f"/Admin/Order/Edit/{order_id}").text


def admin_notes(browser, order_id):
    """The order's notes, as the admin's order page asks for them (Order/OrderNotesSelect), as text."""
    page = admin_order(browser, order_id)
    answer = browser.post("/Admin/Order/OrderNotesSelect", {
        "__RequestVerificationToken": token_of(page), "OrderId": str(order_id), "Draw": "1", "Start": "0", "Length": "100",
    })
    check(answer.status == 200, f"the order's notes answered {answer.status}")
    return [html.unescape(re.sub(r"<[^>]+>", " ", note["Note"])).strip() for note in answer.json()["Data"]]


def admin_received(page):
    """What the admin's Bitcoin payment block says came, for each invoice (the latest first)."""
    return [html.unescape(value).strip() for value in re.findall(r'<th scope="row">Received</th><td>([^<]*)</td>', page)]


def order_status_in_admin(page):
    """The order's status on the admin's order page (the label, then its value)."""
    at = page.find("Order status")
    match = re.search(r">\s*(Pending|Processing|Complete|Cancelled)\s*<", page[at:at + 600]) if at >= 0 else None
    check(match is not None, "the admin's order page has no order status")
    return match.group(1)


def customer_order_page(browser, order_id):
    page = browser.get(f"/orderdetails/{order_id}")
    check(page.status == 200, f"the customer's order page answered {page.status}")
    return page.text


def phase_of(browser, location):
    return browser.get(location.replace("/sattleme/pay/", "/sattleme/status/")).json()["phase"]


def order_block(browser, order_id):
    """The plugin's block on the customer's page of the order (My account, Orders), as text."""
    page = browser.get(f"/orderdetails/{order_id}")
    check(page.status == 200, f"the customer's order page answered {page.status}")
    block = re.search(r'<div class="sattleme-order-payment">(.*?)</div>', page.text, re.S)
    check(block is not None, "the customer's order page has no Bitcoin payment block")
    return html.unescape(re.sub(r"\s+", " ", block.group(1)))


def uninstall(browser):
    """Uninstalls both plugins, as the admin does (Local plugins, Uninstall, then restart): every
    invoice is final, so the payment method lets itself be uninstalled, and its migration's Down
    deletes its tables (e2e/run.sh checks the database)."""
    for system_name in ("Misc.SattlemeE2e", "Payments.Sattleme"):
        page = browser.get("/Admin/Plugin/List").text
        step(f"uninstalling {system_name}")
        answer = browser.post("/Admin/Plugin/List", {
            "__RequestVerificationToken": token_of(page),
            f"uninstall-plugin-link-{system_name}": "",
        })
        check(answer.status == 302, f"uninstalling {system_name} failed: {answer.status}")
    step("restarting nopCommerce to uninstall them")
    restart(browser, lambda: plugins(browser).get("Payments.Sattleme") is False and plugins(browser).get("Misc.SattlemeE2e") is False)


def paid_in_admin(page):
    """The admin's order page says its payment status is Paid (the label, then its value)."""
    at = page.find("Payment status")
    return at >= 0 and re.search(r">\s*Paid\s*<", page[at:at + 600]) is not None


def underpaid_order(browser, placed):
    """An order with a minute to pay: its invoice ends unpaid, its new one ends underpaid."""
    step("a third order, with a minute to pay")
    configure(browser, minutes=1)
    time.sleep(max(0.0, 65 - (time.monotonic() - placed)))
    location = place_order(browser, payment_text="You have 1 minute.")
    page = browser.get(location).text
    order = re.search(r'href="/orderdetails/(\d+)"', page)
    check(order is not None, "no link back to the order")
    order_id = int(order.group(1))
    first = browser.get("/e2e-sattleme/references").json()[-1]

    step("its invoice ends unpaid (a minute, then its 120 seconds of grace): the customer may cancel, or get a new one")
    wait_until("the invoice's end", lambda: phase_of(browser, location) == "expired", 420, every=5)
    page = customer_order_page(browser, order_id)
    cancel = re.search(r'id="cancel_order" href="([^"]+)"', page)
    check(cancel is not None, "Cancel order is not offered once the invoice ended unpaid")
    cancel_url = html.unescape(cancel.group(1))
    check('name="repost-payment"' in page, "no Retry payment once the invoice ended unpaid: a new invoice can be had")
    check("the invoice was not paid in time" in order_block(browser, order_id), "the order's page does not say it ended unpaid")
    form = next((form for form in forms_of(browser.get(location).text) if "/sattleme/" in form["action"]), None)
    check(form is not None, "the payment page offers no new invoice")
    answer = browser.request("POST", form["action"], data=urllib.parse.urlencode(form["fields"]).encode(),
                             headers={"Content-Type": "application/x-www-form-urlencoded"})
    check(answer.status == 302, f"the new invoice was not made: {answer.status}")
    second = browser.get("/e2e-sattleme/references").json()[-1]
    check(second != first, "no new invoice")

    page = browser.get(location).text
    sats = re.search(r'<strong class="sattleme-pay-sats">([\d,]+) sats</strong>', page)
    check(sats is not None, "the payment page shows no amount")
    amount = int(sats.group(1).replace(",", ""))
    came = amount // 2
    check(browser.post(f"/e2e-sattleme/underpay/{second}/{came}", {}).status == 200, "the fake could not underpay")
    step(f"{came} of its {amount} sats came on-chain; the new invoice ends underpaid")
    wait_until("the underpayment", lambda: phase_of(browser, location) == "underpaid", 60, every=5)
    wait_until("the new invoice's end", lambda: phase_of(browser, location) == "expired", 420, every=5)

    # The order's page says what the payment page says, and offers neither "Cancel order" nor
    # "Retry payment", nor a link to the payment page.
    expected = f"nly {came:,} of {amount:,} sats came in time"  # "Only" on the page, "only" in the block
    check(expected in browser.get(location).text, f"the payment page does not say {expected!r}")
    block = order_block(browser, order_id)
    check(expected in block and "will contact you" in block, f"the order's page does not say what came: {block}")
    check("/sattleme/pay/" not in block, f"the order's page leads to the payment page: {block}")
    page = customer_order_page(browser, order_id)
    check('id="cancel_order"' not in page, "Cancel order is offered once an underpayment reached the wallet")
    check('name="repost-payment"' not in page, "Retry payment is offered once an underpayment ended")
    # Asked all the same: refused, the order stays as it is.
    answer = browser.get(cancel_url)
    check(answer.status == 302 and f"/orderdetails/{order_id}" in (answer.location or ""), f"the cancellation answered {answer.status}")
    admin = admin_order(browser, order_id)
    check(order_status_in_admin(admin) == "Pending", "the customer cancelled an order whose underpayment reached the wallet")
    check(admin_received(admin)[:2] == [f"{came:,} sats", "0 sats"], f"the admin's block: {admin_received(admin)}")
    notes = admin_notes(browser, order_id)
    for reference in (first, second):
        check(any(note.startswith(f"Bitcoin invoice {reference} made: ") for note in notes), f"no note of {reference} made: {notes}")
    check(any(note.startswith(f"Bitcoin invoice {first} ended unpaid") for note in notes), f"no note of the first invoice's end: {notes}")
    check(any(note.startswith(f"Bitcoin invoice {second} ended underpaid: {came:,} of {amount:,} sats came") for note in notes),
          f"no note of the underpayment: {notes}")
    step(f"order {order_id}: its underpayment is noted, and the customer can neither cancel it nor retry its payment")


class Screenshots:
    """Screenshots of the shop's pages, as a customer's or the admin's browser shows them: Google
    Chrome, headless, with the cookies of this test's session, driven over its DevTools protocol
    through a pipe (--remote-debugging-pipe: JSON messages, each ended by a NUL byte, that Chrome
    reads on its file descriptor 3 and writes on its 4), so with Python's standard library only.

    Chrome sees the shop as this test does, under its host name (http://localhost/), through the
    shop itself as its HTTP proxy: the requests reach Kestrel in absolute-form, which it takes
    (RFC 9112, 3.2.2; Http1Connection.OnAbsoluteFormTarget), with the same Host header. The shop's
    absolute URLs then work in Chrome too: nopCommerce caches some with the host of the request
    that first built them (the default logo's path: CommonModelFactory.PrepareLogoModelAsync),
    which is this test's.

    Chrome opens nothing but this test's own shop, on this machine: it runs without its sandbox,
    which the runners' kernels may refuse to give it. Each screenshot is a PNG of 1280 pixels in
    width at most, never less than 600 (what nopCommerce's marketplace asks of its pictures), of a
    page with no broken image.
    """

    WIDTH = 1280
    HEIGHT = 1000

    def __init__(self, directory, browser, binary):
        chrome = shutil.which(binary)
        check(chrome is not None, f"no {binary} to take the screenshots with")
        os.makedirs(directory, exist_ok=True)
        self.directory = directory
        self.browser = browser
        self.origin = f"http://{browser.host}"
        self.profile = tempfile.mkdtemp(prefix="sattleme-e2e-chrome-")
        self.buffer = b""
        self.events = []
        self.last = 0
        self.process = None
        # The pipes' ends Chrome gets, moved to descriptors 5 to 9: above 4, so that the shell's
        # moves to 3 and 4 never overwrite one with the other, and of one digit, as POSIX's shell
        # (dash) reads them.
        commands_read, self.commands = os.pipe()
        self.answers, answers_write = os.pipe()
        theirs = [fcntl.fcntl(end, fcntl.F_DUPFD, 5) for end in (commands_read, answers_write)]
        os.close(commands_read)
        os.close(answers_write)
        try:
            check(all(end <= 9 for end in theirs), f"no free descriptors under 10 for Chrome's pipe: {theirs}")
            script = f'exec "$0" "$@" 3<&{theirs[0]} 4>&{theirs[1]} {theirs[0]}<&- {theirs[1]}>&-'
            self.process = subprocess.Popen(
                ["/bin/sh", "-c", script, chrome, "--headless=new", "--remote-debugging-pipe",
                 f"--user-data-dir={self.profile}", "--no-sandbox", "--no-first-run", "--no-default-browser-check",
                 f"--proxy-server={browser.base}", "--proxy-bypass-list=<-loopback>",
                 "--disable-gpu", "--disable-extensions", "--disable-sync",
                 "--disable-background-networking", "--hide-scrollbars", "--mute-audio", "about:blank"],
                stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, pass_fds=theirs)
        except BaseException:
            for end in theirs:
                os.close(end)
            self.close()
            raise
        for end in theirs:
            os.close(end)
        try:
            target = self.call("Target.createTarget", {"url": "about:blank"})["targetId"]
            self.session = self.call("Target.attachToTarget", {"targetId": target, "flatten": True})["sessionId"]
            self.call("Page.enable", session=self.session)
            self.call("Emulation.setDeviceMetricsOverride",
                      {"width": self.WIDTH, "height": self.HEIGHT, "deviceScaleFactor": 1, "mobile": False}, session=self.session)
        except BaseException:
            self.close()
            raise

    def read(self, deadline):
        while b"\0" not in self.buffer:
            left = deadline - time.monotonic()
            check(left > 0, "Chrome did not answer in time")
            ready, _, _ = select.select([self.answers], [], [], left)
            if ready:
                chunk = os.read(self.answers, 1 << 20)
                check(len(chunk) > 0, "Chrome stopped")
                self.buffer += chunk
        message, _, self.buffer = self.buffer.partition(b"\0")
        return json.loads(message)

    def call(self, method, params=None, session=None, timeout=60):
        self.last += 1
        message = {"id": self.last, "method": method, "params": params or {}}
        if session is not None:
            message["sessionId"] = session
        data = json.dumps(message).encode() + b"\0"
        while data:
            data = data[os.write(self.commands, data):]
        deadline = time.monotonic() + timeout
        while True:
            answer = self.read(deadline)
            if answer.get("id") == self.last:
                check("error" not in answer, f"Chrome refused {method}: {answer.get('error')}")
                return answer.get("result", {})
            if "method" in answer:
                self.events.append(answer)

    def wait_for(self, method, timeout=60):
        deadline = time.monotonic() + timeout
        while True:
            for event in self.events:
                if event["method"] == method and event.get("sessionId") == self.session:
                    self.events.remove(event)
                    return event
            answer = self.read(deadline)
            if "method" in answer:
                self.events.append(answer)

    def evaluate(self, expression):
        result = self.call("Runtime.evaluate", {"expression": expression, "returnByValue": True, "awaitPromise": True},
                           session=self.session)
        check("exceptionDetails" not in result, f"the page's script failed: {result.get('exceptionDetails')}")
        return result["result"].get("value")

    def take(self, name, path, selector, frame):
        """The page at path, once loaded with its fonts: the whole first screen ("screen"), the page
        from its top down to the element of selector ("down_to"), or that element alone
        ("element"), with a margin; the element must be on the page either way."""
        self.events.clear()
        self.resize(self.HEIGHT)
        cookies = [{"name": cookie.name, "value": cookie.value, "url": self.origin + "/", "path": cookie.path or "/",
                    "httpOnly": cookie.has_nonstandard_attr("HttpOnly")} for cookie in self.browser.jar]
        if cookies:
            self.call("Network.setCookies", {"cookies": cookies}, session=self.session)
        navigated = self.call("Page.navigate", {"url": self.origin + path}, session=self.session)
        check("errorText" not in navigated, f"Chrome could not open {path}: {navigated.get('errorText')}")
        self.wait_for("Page.loadEventFired")
        self.evaluate("document.fonts.ready.then(() => true)")
        time.sleep(1)
        check(self.evaluate("location.pathname") == urllib.parse.urlsplit(path).path,
              f"Chrome was sent elsewhere than {path}: {self.evaluate('location.href')}")
        broken = self.evaluate("[...document.images].filter(image => image.complete && image.naturalWidth === 0).map(image => image.src)")
        check(broken == [], f"broken images on {path} in Chrome: {broken}")
        measure = ("(() => { const element = document.querySelector(" + json.dumps(selector) + "); if (!element) return null;"
                   " const box = element.getBoundingClientRect();"
                   " return { x: box.left + window.scrollX, y: box.top + window.scrollY, width: box.width, height: box.height,"
                   " top: window.scrollY }; })()")
        box = self.evaluate(measure)
        check(box is not None, f"no {selector} on {path} in Chrome")
        check(box["width"] > 0 and box["height"] > 0, f"{selector} is not shown on {path} in Chrome")
        check(frame in ("screen", "down_to", "element"), f"not a frame of a screenshot: {frame}")
        # Taken inside the window, never beyond it (a capture beyond the window lays the page out
        # again, at another size, after the measure): the window made taller until what is taken
        # fits in it, and measured again, as long as the page does not grow with it.
        height = self.HEIGHT
        for _ in range(4):
            needed = math.ceil(box["y"] + box["height"] + 24 if frame == "down_to" else box["height"] + 32)
            if frame == "screen" or needed <= height:
                break
            check(needed <= 8000, f"{selector} on {path} is too tall to be taken")
            height = needed
            self.resize(height)
            box = self.evaluate(measure)
        else:
            check(False, f"{selector} on {path} moves as the window grows: it cannot be taken")
        params = {"format": "png"}
        if frame == "down_to":
            self.evaluate("window.scrollTo(0, 0), true")
            box = self.evaluate(measure)
            params["clip"] = {"x": 0, "y": 0, "width": self.WIDTH, "height": min(height, box["y"] + box["height"] + 24), "scale": 1}
        elif frame == "element":
            self.evaluate(f"window.scrollTo(0, {max(0.0, box['y'] - 16)}), true")
            box = self.evaluate(measure)
            x = max(0.0, box["x"] - 16)
            y = max(box["top"], box["y"] - 16)
            bottom = min(box["top"] + height, box["y"] + box["height"] + 16)
            params["clip"] = {"x": x, "y": y, "width": min(self.WIDTH - x, box["width"] + 32), "height": bottom - y, "scale": 1}
        png = base64.b64decode(self.call("Page.captureScreenshot", params, session=self.session, timeout=120)["data"])
        check(png[:8] == b"\x89PNG\r\n\x1a\n", f"the screenshot of {path} is not a PNG")
        width, height = struct.unpack(">II", png[16:24])
        check(600 <= width <= self.WIDTH, f"the screenshot of {path} is {width} pixels wide")
        file = os.path.join(self.directory, f"{name}.png")
        with open(file, "wb") as output:
            output.write(png)
        step(f"screenshot {file} ({width}×{height})")

    def resize(self, height):
        """The window 1280 pixels wide and height tall, once the page is laid out again."""
        self.call("Emulation.setDeviceMetricsOverride",
                  {"width": self.WIDTH, "height": height, "deviceScaleFactor": 1, "mobile": False}, session=self.session)
        self.evaluate("new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))")

    def close(self):
        """Closes Chrome (killed if it does not stop) and removes its profile; once."""
        if self.process is not None:
            try:
                self.call("Browser.close", timeout=10)
            except (Failure, OSError):
                pass
            try:
                self.process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
            self.process = None
        for end in ("commands", "answers"):
            if getattr(self, end) is not None:
                os.close(getattr(self, end))
                setattr(self, end, None)
        shutil.rmtree(self.profile, ignore_errors=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", required=True)
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--pg-host", required=True)
    parser.add_argument("--pg-password", required=True)
    parser.add_argument("--plugin", required=True)
    parser.add_argument("--fake", required=True)
    parser.add_argument("--screenshots", help="the directory to keep screenshots of the plugin's pages in")
    parser.add_argument("--chrome", default="google-chrome", help="the Google Chrome to take them with")
    args = parser.parse_args()
    browser = Browser(args.base, args.host)
    screenshots = None
    try:
        install(browser, args)
        login(browser)
        upload_and_install(browser, [args.plugin, args.fake])
        if args.screenshots:
            screenshots = Screenshots(args.screenshots, browser, args.chrome)
        run(browser, screenshots)
    finally:
        if screenshots is not None:
            screenshots.close()


def run(browser, screenshots):
    """What a merchant and a customer do, once the plugin is installed."""
    configure(browser)
    if screenshots is not None:
        screenshots.take("2-configuration", "/Admin/Sattleme/Configure",
                         'form[action$="/Sattleme/Configure"], form[action$="/SattlemeAdmin/Configure"]', "screen")

    step("placing an order with the Bitcoin payment method")
    location = place_order(browser)
    placed = time.monotonic()
    step(f"on the payment page {location[:32]}…")
    order_id = check_page(browser, location)
    if screenshots is not None:
        screenshots.take("1-payment-page", location, "#sattleme-pay", "down_to")
    reference = pay_last_invoice(browser)
    step(f"the fake paid {reference}; the page's status checks it")
    time.sleep(6)
    status = browser.get(location.replace("/sattleme/pay/", "/sattleme/status/")).json()
    check(status["phase"] == "paid", f"the status after the payment is {status}")
    page = browser.get(location).text
    check("your order is paid" in page, "the payment page does not say the order is paid")
    order = admin_order(browser, order_id)
    check(reference in order, "the admin's order page does not show the reference")
    check("Proof of payment (preimage)" in order, "the admin's order page does not show the proof of payment")
    check(paid_in_admin(order), "the order is not paid in the admin area")
    check("Bitcoin payment:</strong> paid." in order_block(browser, order_id), "the customer's order page does not say it is paid")
    # Paid in Lightning: the admin sees the Lightning invoice's amount, not "0 sats".
    received = admin_received(order)
    check(len(received) == 1 and re.fullmatch(r"[\d,]+ sats, in Lightning \(its Lightning invoice\)", received[0]) is not None
          and not received[0].startswith("0 "), f"the admin's block does not say what came in Lightning: {received}")
    # The order's notes: its invoice made at placement (noted once nopCommerce placed the order),
    # then paid, never with an address.
    notes = admin_notes(browser, order_id)
    made = [note for note in notes if note.startswith(f"Bitcoin invoice {reference} made: ")]
    check(len(made) == 1 and re.search(r"sats, payable in Lightning and on-chain until \d{4}-\d\d-\d\d \d\d:\d\d:\d\d UTC\.$", made[0]),
          f"no note of the invoice made at placement: {notes}")
    check(any(note.startswith(f"Bitcoin invoice {reference} paid in Lightning: its Lightning invoice of ") for note in notes),
          f"no note of the Lightning payment: {notes}")
    check(not any("bc1p" in note or "lnbc" in note for note in notes), "a note holds an address or an invoice")
    step(f"order {order_id} is paid, its capture transaction {reference}; its notes and the admin's block say so")
    if screenshots is not None:
        screenshots.take("3-admin-order", f"/Admin/Order/Edit/{order_id}", "#order-sattleme", "element")

    step("a second order, paid with no page open: the background checks find it")
    # nopCommerce's minimum interval between two orders of a customer: a minute
    # (OrderSettings.MinimumOrderPlacementInterval, InstallRequiredData.cs:1880).
    time.sleep(max(0.0, 65 - (time.monotonic() - placed)))
    location = place_order(browser)
    second = check_page(browser, location)
    block = order_block(browser, second)
    check("waiting for your payment" in block and f'href="{location}"' in block,
          f"the customer's order page does not lead to the payment page: {block}")
    # Its invoice can be paid: "Retry payment" leads to it; the customer may not cancel the order.
    page = customer_order_page(browser, second)
    check('name="repost-payment"' in page, "no Retry payment while the invoice can be paid")
    check('id="cancel_order"' not in page, "Cancel order is offered while the invoice can be paid")
    reference = pay_last_invoice(browser)
    wait_until("the background check (every minute)", lambda: paid_in_admin(admin_order(browser, second)), 240, every=10)
    placed = time.monotonic()
    step(f"order {second} is paid by the scheduled task")

    underpaid_order(browser, placed)
    uninstall(browser)
    print("[e2e] passed", flush=True)


if __name__ == "__main__":
    try:
        main()
    except Failure as failure:
        print(f"[e2e] FAILED: {failure}", file=sys.stderr, flush=True)
        sys.exit(1)
