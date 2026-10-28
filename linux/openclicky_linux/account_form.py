"""The account form, shared by the setup window and Settings: the Mac's email-first sign-in
(`EmailAccountForm.swift`). Signed out: an email field ("start"); an email with an account asks for
the 6-digit code it was sent. Signed in: who, the month's allowance and whether the email is
confirmed (both from `GET /billing/me`, with a field for the confirmation code), and sign out. Every request runs off the main thread; a reply only changes the
form if the form still shows what that request started from.
"""

from __future__ import annotations

import threading
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
from gi.repository import GLib, Gtk  # noqa: E402

from . import accounts  # noqa: E402
from .config import load_settings  # noqa: E402


def _in_background(work: Callable[[], object], done: Callable[[object], None]) -> None:
    def run() -> None:
        try:
            outcome = work()
        except Exception as failure:  # noqa: BLE001 - shown in the form
            outcome = failure
        GLib.idle_add(lambda: (done(outcome), False)[1])
    threading.Thread(target=run, daemon=True).start()


class AccountForm:
    def __init__(self, say: Callable[[str, bool | None], None], on_changed: Callable[[], None] = lambda: None) -> None:
        """`say(sentence, good)` shows a line under the form (good: True ✓, False ✕, None neutral)."""
        self.say = say
        self.on_changed = on_changed
        self.box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.signed_in_label = Gtk.Label(xalign=0)
        self.signed_in_label.set_line_wrap(True)
        self.box.pack_start(self.signed_in_label, False, False, 0)
        self.email_entry = Gtk.Entry()
        self.email_entry.set_placeholder_text("your email")
        self.email_button = Gtk.Button(label="start")
        self.email_button.connect("clicked", lambda _button: self.start())
        self.email_entry.connect("activate", lambda _entry: self.start())
        self.email_row = self._row(self.email_entry, self.email_button)
        self.code_entry = Gtk.Entry()
        self.code_entry.set_placeholder_text("6-digit code")
        self.code_entry.set_max_length(8)
        self.code_button = Gtk.Button(label="confirm")
        self.code_button.connect("clicked", lambda _button: self.submit_code())
        self.code_entry.connect("activate", lambda _entry: self.submit_code())
        self.code_row = self._row(self.code_entry, self.code_button)
        self.sign_out_button = Gtk.Button(label="sign out")
        self.sign_out_button.set_halign(Gtk.Align.START)
        self.sign_out_button.connect("clicked", lambda _button: self.sign_out())
        self.box.pack_start(self.sign_out_button, False, False, 0)
        for widget in (self.code_row, self.sign_out_button, self.signed_in_label):
            widget.set_no_show_all(True)
        # The email a code was sent to (sign in with it), or None; while signed in, a code confirms.
        self.waiting_for_code_from: str | None = None
        self._request = 0
        # The allowance's own count: asking for it never makes a sign-in's reply look stale.
        self._standing_request = 0

    def _row(self, entry: Gtk.Entry, button: Gtk.Button) -> Gtk.Box:
        row = Gtk.Box(spacing=8)
        row.pack_start(entry, True, True, 0)
        row.pack_start(button, False, False, 0)
        self.box.pack_start(row, False, False, 0)
        return row

    def _show(self, widget: Gtk.Widget, visible: bool) -> None:
        widget.set_no_show_all(not visible)
        if visible:
            widget.show_all()
        else:
            widget.hide()

    def refresh(self, ask_the_backend: bool = True) -> None:
        """Shows the form for the account now in shell.json, then (in the background) its allowance
        and whether its email still needs confirming."""
        settings = load_settings()
        signed_in = bool(settings.account_token)
        if signed_in:
            who = settings.account_email or "a guest, with no email yet"
            self.signed_in_label.set_text(f"signed in: {who}")
        self._show(self.signed_in_label, signed_in)
        self._show(self.sign_out_button, signed_in and bool(settings.refresh_token or settings.account_email))
        self._show(self.email_row, not signed_in)
        self._show(self.code_row, self.waiting_for_code_from is not None or (signed_in and bool(settings.account_email)
                                                                           and self._unconfirmed))
        if signed_in and self._unconfirmed:
            self.code_entry.set_placeholder_text("code from the confirmation email")
        if signed_in and ask_the_backend:
            self._standing_request += 1
            request = self._standing_request
            _in_background(lambda: accounts.billing(settings), lambda standing: self._show_standing(standing, request))

    def _show_standing(self, standing, request: int) -> None:
        if request != self._standing_request or not isinstance(standing, dict) or not load_settings().account_token:
            return
        confirmed = standing.get("confirmed") is not False
        spent, limit = (("guestSpentUsd", "guestLimitUsd") if not confirmed else ("spentMonthUsd", "monthlyLimitUsd"))
        line = self.signed_in_label.get_text()
        if isinstance(standing.get(limit), (int, float)) and standing[limit] > 0:
            line += f" · ${standing.get(spent) or 0:.2f} of ${standing[limit]:.2f} used{'' if not confirmed else ' this month'}"
        self.signed_in_label.set_text(line)
        if self._unconfirmed != (not confirmed):
            self._unconfirmed = not confirmed
            self.refresh(ask_the_backend=False)
            self.signed_in_label.set_text(line)

    _unconfirmed = False

    def start(self) -> None:
        email = self.email_entry.get_text().strip()
        if "@" not in email or "." not in email.split("@")[-1]:
            self.say("that doesn't look like an email yet.", False)
            return
        self._request += 1
        request = self._request
        self.email_button.set_sensitive(False)
        self.say("one moment…", None)

        def answered(outcome) -> None:
            if request != self._request:
                return
            self.email_button.set_sensitive(True)
            if isinstance(outcome, Exception):
                self.say(accounts.UNREACHABLE, False)
            elif outcome.kind == "code_sent":
                self.waiting_for_code_from = email
                self.refresh()
                self.code_entry.grab_focus()
                self.say(f"that email has an account. type the 6-digit code i sent to {email}.", None)
            elif outcome.kind == "signed_in":
                self._signed_in(outcome)
            else:
                self.say(outcome.message or accounts.UNREACHABLE, False)
        _in_background(lambda: accounts.start(email), answered)

    def submit_code(self) -> None:
        code = self.code_entry.get_text()
        if len([character for character in code if character.isdigit()]) != 6:
            self.say("the code is the 6 digits in the email.", False)
            return
        self._request += 1
        request = self._request
        self.code_button.set_sensitive(False)
        email = self.waiting_for_code_from

        def answered(outcome) -> None:
            if request != self._request:
                return
            self.code_button.set_sensitive(True)
            if isinstance(outcome, Exception):
                self.say(accounts.UNREACHABLE, False)
            elif outcome.kind in ("signed_in", "already_confirmed"):
                self.code_entry.set_text("")
                self._signed_in(outcome)
            else:
                self.say(outcome.message or accounts.BAD_CODE, False)
        # A code sent for an email signs in with it; signed in already, it confirms the email.
        work = (lambda: accounts.submit_code(email, code)) if email else (lambda: accounts.confirm(code))
        _in_background(work, answered)

    def _signed_in(self, outcome: accounts.Outcome) -> None:
        self.waiting_for_code_from = None
        self._unconfirmed = not outcome.confirmed
        self.refresh()
        if outcome.confirmed:
            self.say("✓ you're signed in. answers come from your free account.", True)
        else:
            self.say("✓ you're on a free account, it works now. the email has a code: type it here to confirm.", True)
        self.on_changed()

    def sign_out(self) -> None:
        accounts.sign_out()
        self.waiting_for_code_from = None
        self._unconfirmed = False
        self.refresh()
        self.say("signed out.", None)
        self.on_changed()
