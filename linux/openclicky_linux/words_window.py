"""The Words window: the Mac window's "Your words" and "How it writes" pages, in three tabs.

- **History**: this week's counts and a bar for each day, every take newest first with the app and
  the time, a search over what was said and written, a filter (dictated, edits, copied, couldn't
  finish); a take's text can be copied, edited (the old text kept as a revision) or deleted, the
  whole history cleared, a take that couldn't finish retried from its kept audio, and a question
  asked across the takes ("what did i say about the budget?", answered by the clean-up model from
  the takes that match). Nothing in it leaves the computer unless a question is asked.
- **Try it here**: a box to dictate into and see what comes out.
- **Styles**: how each kind of app is written (developer, work messaging, personal messaging,
  email, other): the rules the clean-up model reads, sentence case, fillers, whether the model
  cleans up at all, and which apps (window classes, or the Mac's bundle ids) use it.

In the main window (`embedded`) the same pieces are two sidebar pages, as on the Mac: "Your words"
(try it here, then the history) and "How it writes" (dictionary, shortcuts, styles).
- **Dictionary**: names spelled your way ("you say aditya shatriya → it writes Aaditya Kshatriya").
- **Shortcuts**: say exactly the trigger, get the saved text.

Every list is the Mac's own file under `~/.openclicky/dictation` (`space.save_words`, `save_styles`).
"""

from __future__ import annotations

import dataclasses
import threading
from datetime import datetime, timedelta
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402

from .config import load_settings  # noqa: E402
from .history import TakeAudioStore, TakeRecord, TakeStore  # noqa: E402
from .polish import ask_history  # noqa: E402
from .space import DictationStyle, DictionaryTerm, SpokenShortcut, load_space, save_styles, save_words  # noqa: E402

HISTORY_LIMIT = 300
# The filter menu: its id → the takes it shows.
FILTERS = (("all", "Everything"), ("dictate", "Dictated"), ("edit", "Hey Clicky edits"), ("clipboard", "Copied"),
           ("failed", "Couldn't finish"))


def shown_by_filter(take: TakeRecord, chosen: str) -> bool:
    if chosen == "failed":
        return take.status == "failed"
    if take.status == "cancelled":
        return False
    return chosen == "all" or take.mode == chosen


def row_text(take: TakeRecord) -> str:
    if take.status == "failed":
        return f"couldn't finish: {take.failure_reason or 'no words'}"
    return take.display_text


def words_per_day(takes: list[TakeRecord], now: datetime | None = None, days: int = 7) -> list[tuple[str, int]]:
    """(weekday letter, words dictated that day), oldest first, for the bars."""
    today = (now or datetime.now()).date()
    counts = {today - timedelta(days=offset): 0 for offset in range(days)}
    for take in takes:
        day = datetime.fromtimestamp(take.created_at).date()
        if day in counts and take.status == "complete" and take.mode != "clipboard":
            counts[day] += take.word_count
    return [(day.strftime("%a")[0], counts[day]) for day in sorted(counts)]


def when(timestamp: float, now: datetime | None = None) -> str:
    """"14:05" today, "yesterday 14:05", else "23 Oct 14:05"."""
    now = now or datetime.now()
    moment = datetime.fromtimestamp(timestamp)
    days = (now.date() - moment.date()).days
    clock = moment.strftime("%H:%M")
    if days == 0:
        return clock
    if days == 1:
        return f"yesterday {clock}"
    return f"{moment.day} {moment.strftime('%b')} {clock}"


class WordsWindow:
    def __init__(self, take_store: TakeStore | None, on_closed: Callable[[], None] = lambda: None,
                 take_audio: TakeAudioStore | None = None, retry: Callable[[str, Callable[[str | None], None]], None] | None = None,
                 embedded: bool = False) -> None:
        self.take_store = take_store
        self.take_audio = take_audio or TakeAudioStore()
        self.retry = retry
        self._days: list[tuple[str, int]] = []
        history = self._history_page()
        self.dictionary_editor = PairEditor("you say", "it writes", self._save_words)
        dictionary = self._page(self.dictionary_editor.box, "Names and words spelled your way. Separate several ways it is heard with commas.")
        self.shortcuts_editor = PairEditor("you say exactly", "it writes", self._save_words, multiline_value=True)
        shortcuts = self._page(self.shortcuts_editor.box, "Say the whole trigger on its own and the saved text goes in.")
        self.try_it_here = Gtk.TextView()
        self.try_it_here.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.try_it_here.set_size_request(-1, 120 if embedded else 200)
        try_scroller = Gtk.ScrolledWindow()
        try_scroller.add(self.try_it_here)
        try_here = self._page(try_scroller, "Click in the box, hold your dictation key and speak: what you say is written here, cleaned "
                                            "up the way it would be anywhere else.")
        self.styles_editor = StylesEditor()
        styles = self._page(self.styles_editor.box, "How each kind of app is written. The rules are what the clean-up model reads.")
        self.window = None
        if embedded:
            self.words_page = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
            self.words_page.set_border_width(10)
            try_here.set_vexpand(False)
            self.words_page.pack_start(try_here, False, False, 0)
            self.words_page.pack_start(history, True, True, 0)
            self.writing_page = Gtk.Notebook()
            self.writing_page.set_border_width(10)
            for page, title in ((dictionary, "Dictionary"), (shortcuts, "Shortcuts"), (styles, "Styles")):
                self.writing_page.append_page(page, Gtk.Label(label=title))
            return
        self.window = Gtk.Window(title="OpenClicky — your words")
        self.window.set_default_size(680, 560)
        self.window.set_border_width(12)
        self.window.connect("delete-event", lambda *_: self.window.hide() or on_closed() or True)
        self.notebook = Gtk.Notebook()
        self.window.add(self.notebook)
        for page, title in ((history, "History"), (dictionary, "Dictionary"), (shortcuts, "Shortcuts"), (try_here, "Try it here"),
                            (styles, "Styles")):
            self.notebook.append_page(page, Gtk.Label(label=title))

    @staticmethod
    def _page(content: Gtk.Widget, explanation: str) -> Gtk.Box:
        page = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        page.set_border_width(8)
        label = Gtk.Label(label=explanation, xalign=0)
        label.set_line_wrap(True)
        label.get_style_context().add_class("dim-label")
        page.pack_start(label, False, False, 0)
        page.pack_start(content, True, True, 0)
        return page

    # MARK: history

    def _history_page(self) -> Gtk.Box:
        page = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        page.set_border_width(8)
        self.stats_label = Gtk.Label(xalign=0)
        page.pack_start(self.stats_label, False, False, 0)
        self.chart = Gtk.DrawingArea()
        self.chart.set_size_request(-1, 56)
        self.chart.connect("draw", self._draw_chart)
        page.pack_start(self.chart, False, False, 0)
        self.question = Gtk.Entry()
        self.question.set_placeholder_text("ask about what you said (“what did i say about the budget?”), press enter")
        self.question.connect("activate", lambda _entry: self._ask())
        page.pack_start(self.question, False, False, 0)
        self.answer = Gtk.Label(xalign=0)
        self.answer.set_line_wrap(True)
        self.answer.set_selectable(True)
        page.pack_start(self.answer, False, False, 0)
        top = Gtk.Box(spacing=8)
        self.search = Gtk.SearchEntry()
        self.search.set_placeholder_text("search what you said")
        self.search.connect("search-changed", lambda _entry: self.show_history())
        top.pack_start(self.search, True, True, 0)
        self.filter = Gtk.ComboBoxText()
        for value, label in FILTERS:
            self.filter.append(value, label)
        self.filter.set_active_id("all")
        self.filter.connect("changed", lambda _combo: self.show_history())
        top.pack_start(self.filter, False, False, 0)
        clear_button = Gtk.Button(label="Delete all…")
        clear_button.connect("clicked", lambda _button: self._delete_all())
        top.pack_start(clear_button, False, False, 0)
        page.pack_start(top, False, False, 0)
        # id, when, app, text
        self.history_rows = Gtk.ListStore(str, str, str, str)
        self.history_view = Gtk.TreeView(model=self.history_rows)
        for index, title in ((1, "when"), (2, "app")):
            column = Gtk.TreeViewColumn(title, Gtk.CellRendererText(), text=index)
            self.history_view.append_column(column)
        text_renderer = Gtk.CellRendererText()
        text_renderer.set_property("wrap-width", 420)
        text_renderer.set_property("wrap-mode", 2)  # Pango.WrapMode.WORD_CHAR
        text_renderer.set_property("editable", True)
        text_renderer.connect("edited", self._edited)
        self.history_view.append_column(Gtk.TreeViewColumn("what was written (double-click to edit)", text_renderer, text=3))
        scroller = Gtk.ScrolledWindow()
        scroller.add(self.history_view)
        page.pack_start(scroller, True, True, 0)
        actions = Gtk.Box(spacing=8)
        for label, action in (("Copy", self._copy_selected), ("Retry", self._retry_selected), ("Delete", self._delete_selected)):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _button, action=action: action())
            actions.pack_start(button, False, False, 0)
        self.history_status = Gtk.Label(xalign=0)
        actions.pack_start(self.history_status, True, True, 0)
        page.pack_start(actions, False, False, 0)
        return page

    def show_history(self) -> None:
        self.history_rows.clear()
        if self.take_store is None:
            self.stats_label.set_text("The history could not be opened, so takes are not kept.")
            return
        stats = self.take_store.stats()
        self.stats_label.set_markup(f"<b>{stats.words_today}</b> words today · <b>{stats.words_this_week}</b> this week · "
                                    f"{stats.all_time_takes} takes in all, kept only on this computer")
        chosen = self.filter.get_active_id() or "all"
        for take in self.take_store.recent(limit=HISTORY_LIMIT, query=self.search.get_text() or None):
            if shown_by_filter(take, chosen):
                self.history_rows.append([take.id, when(take.created_at), take.app_name or "", row_text(take)])
        self._days = words_per_day(self.take_store.recent(limit=2000))
        self.chart.queue_draw()

    def _draw_chart(self, area, context) -> bool:
        if not self._days:
            return False
        width, height = area.get_allocated_width(), area.get_allocated_height()
        most = max(count for _day, count in self._days) or 1
        slot = width / len(self._days)
        colour = area.get_style_context().get_color(Gtk.StateFlags.NORMAL)
        for index, (letter, count) in enumerate(self._days):
            bar_height = (height - 16) * count / most
            context.set_source_rgba(0.93, 0.36, 0.2, 0.9 if index == len(self._days) - 1 else 0.55)
            context.rectangle(index * slot + slot * 0.25, height - 14 - bar_height, slot * 0.5, max(bar_height, 1))
            context.fill()
            context.set_source_rgba(colour.red, colour.green, colour.blue, 0.6)
            context.move_to(index * slot + slot * 0.5 - 3, height - 2)
            context.show_text(letter)
        return False

    def _ask(self) -> None:
        question = self.question.get_text().strip()
        if not question or self.take_store is None:
            return
        self.answer.set_text("asking…")
        takes = self.take_store.matches_for(question)
        settings = load_settings()

        def run() -> None:
            answered = ask_history(question, takes, settings)
            GLib.idle_add(lambda: (self.answer.set_text(answered), False)[1])

        threading.Thread(target=run, daemon=True).start()

    def _retry_selected(self) -> None:
        take_id, _row = self._selected_take_id()
        take = self.take_store.fetch(take_id) if take_id and self.take_store else None
        if take is None or take.status != "failed":
            self.history_status.set_text("pick a take that couldn't finish.")
            return
        if self.retry is None or not self.take_audio.has_audio(take_id):
            self.history_status.set_text("its recording wasn't kept, so it can't be retried.")
            return
        self.history_status.set_text("retrying…")

        def done(problem: str | None) -> None:
            self.history_status.set_text(problem or "retried · copied, press ctrl+v")
            self.show_history()

        self.retry(take_id, done)

    def _selected_take_id(self) -> tuple[str | None, Gtk.TreeIter | None]:
        model, row = self.history_view.get_selection().get_selected()
        return (model[row][0], row) if row is not None else (None, None)

    def _copy_selected(self) -> None:
        take_id, row = self._selected_take_id()
        if take_id is None:
            self.history_status.set_text("pick a take first.")
            return
        Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD).set_text(self.history_rows[row][3], -1)
        self.history_status.set_text("copied.")

    def _delete_selected(self) -> None:
        take_id, row = self._selected_take_id()
        if take_id is None or self.take_store is None:
            return
        self.take_store.delete(take_id)
        self.take_audio.discard(take_id)
        self.history_rows.remove(row)
        self.history_status.set_text("deleted.")

    def _edited(self, _renderer, path: str, new_text: str) -> None:
        if self.take_store is None or not new_text.strip():
            return
        take_id = self.history_rows[path][0]
        take = self.take_store.fetch(take_id)
        if take is None or take.status != "complete":
            return
        self.take_store.revise(take_id, new_text, editor="history")
        self.history_rows[path][3] = new_text

    def _delete_all(self) -> None:
        if self.take_store is None:
            return
        dialog = Gtk.MessageDialog(transient_for=self.window, modal=True, message_type=Gtk.MessageType.QUESTION,
                                   buttons=Gtk.ButtonsType.OK_CANCEL, text="Delete every take in the history?")
        dialog.format_secondary_text("This cannot be undone. Your dictionary and shortcuts stay.")
        answer = dialog.run()
        dialog.destroy()
        if answer == Gtk.ResponseType.OK:
            self.take_store.delete_all()
            self.take_audio.discard_all()
            self.show_history()

    # MARK: dictionary and shortcuts

    def _load_words(self) -> None:
        space = load_space()
        self._terms = {term.written: term for term in space.dictionary}
        self._shortcuts = {shortcut.trigger: shortcut for shortcut in space.shortcuts}
        self.dictionary_editor.set_pairs([(", ".join(term.heard_as), term.written) for term in space.dictionary])
        self.shortcuts_editor.set_pairs([(shortcut.trigger, shortcut.replacement) for shortcut in space.shortcuts])

    def _save_words(self) -> None:
        terms = []
        for heard, written in self.dictionary_editor.pairs():
            known = self._terms.get(written)
            heard_as = tuple(alias.strip() for alias in heard.split(",") if alias.strip())
            terms.append(DictionaryTerm(written, heard_as, id=known.id if known else "", created_at=known.created_at if known else 0.0))
        shortcuts = []
        for trigger, replacement in self.shortcuts_editor.pairs():
            known = self._shortcuts.get(trigger)
            shortcuts.append(SpokenShortcut(trigger, replacement, id=known.id if known else "", created_at=known.created_at if known else 0.0))
        save_words(terms, shortcuts)
        self._load_words()

    def refresh(self) -> None:
        """Everything read again: the history, the words, the styles."""
        self.show_history()
        self._load_words()
        self.styles_editor.load()

    def show(self, page: int = 0) -> None:
        self.refresh()
        self.window.show_all()
        self.notebook.set_current_page(page)
        self.window.present()


class StylesEditor:
    """The styles, one at a time: pick one on the left, change it on the right, Save."""

    def __init__(self) -> None:
        self.styles: list[DictationStyle] = []
        self.list_rows = Gtk.ListStore(str, str)  # id, name
        self.list_view = Gtk.TreeView(model=self.list_rows)
        self.list_view.set_headers_visible(False)
        self.list_view.append_column(Gtk.TreeViewColumn("style", Gtk.CellRendererText(), text=1))
        self.list_view.get_selection().connect("changed", lambda _selection: self._show_selected())
        self.rules = Gtk.TextView()
        self.rules.set_wrap_mode(Gtk.WrapMode.WORD)
        self.rules.set_size_request(-1, 110)
        rules_scroller = Gtk.ScrolledWindow()
        rules_scroller.add(self.rules)
        self.sentence_case = Gtk.CheckButton(label="Capital letters at the start of sentences")
        self.remove_fillers = Gtk.CheckButton(label="Leave out “um” and “uh”")
        self.polish_with_model = Gtk.CheckButton(label="Let the AI clean-up rewrite takes in this style")
        self.apps = Gtk.Entry()
        self.apps.set_placeholder_text("window classes, separated by commas (slack, code, firefox)")
        self.status = Gtk.Label(xalign=0)
        save = Gtk.Button(label="Save style")
        save.set_halign(Gtk.Align.START)
        save.connect("clicked", lambda _button: self.save())
        detail = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        detail.pack_start(Gtk.Label(label="The rules the model reads", xalign=0), False, False, 0)
        detail.pack_start(rules_scroller, True, True, 0)
        for widget in (self.sentence_case, self.remove_fillers, self.polish_with_model):
            detail.pack_start(widget, False, False, 0)
        detail.pack_start(Gtk.Label(label="Apps written in this style", xalign=0), False, False, 0)
        detail.pack_start(self.apps, False, False, 0)
        detail.pack_start(save, False, False, 0)
        detail.pack_start(self.status, False, False, 0)
        self.box = Gtk.Box(spacing=12)
        self.list_view.set_size_request(170, -1)
        self.box.pack_start(self.list_view, False, False, 0)
        self.box.pack_start(detail, True, True, 0)

    def load(self) -> None:
        chosen = self._selected_index()
        self.styles = list(load_space().styles)
        self.list_rows.clear()
        for style in self.styles:
            self.list_rows.append([style.id, style.name])
        if self.styles:
            self.list_view.get_selection().select_path(Gtk.TreePath(min(chosen or 0, len(self.styles) - 1)))
        self.status.set_text("")

    def _selected_index(self) -> int | None:
        _model, row = self.list_view.get_selection().get_selected()
        return self.list_rows.get_path(row).get_indices()[0] if row is not None else None

    def _show_selected(self) -> None:
        index = self._selected_index()
        if index is None or index >= len(self.styles):
            return
        style = self.styles[index]
        self.rules.get_buffer().set_text(style.rules)
        self.sentence_case.set_active(style.sentence_case)
        self.remove_fillers.set_active(style.remove_fillers)
        self.polish_with_model.set_active(style.polish_with_model)
        self.apps.set_text(", ".join(style.apps))

    def save(self) -> None:
        index = self._selected_index()
        if index is None:
            return
        buffer = self.rules.get_buffer()
        apps = tuple(dict.fromkeys(app.strip() for app in self.apps.get_text().split(",") if app.strip()))
        self.styles[index] = dataclasses.replace(
            self.styles[index], rules=buffer.get_text(buffer.get_start_iter(), buffer.get_end_iter(), False).strip(),
            sentence_case=self.sentence_case.get_active(), remove_fillers=self.remove_fillers.get_active(),
            polish_with_model=self.polish_with_model.get_active(), apps=apps)
        try:
            save_styles(self.styles)
        except (OSError, ValueError) as failure:
            self.status.set_text(f"Not saved: {failure}")
            return
        self.status.set_text("Saved. The next take uses it.")


class PairEditor:
    """A list of (key, value) rows with add and remove; every change is saved at once."""

    def __init__(self, key_title: str, value_title: str, on_changed: Callable[[], None], multiline_value: bool = False) -> None:
        self.on_changed = on_changed
        self.rows = Gtk.ListStore(str, str)
        self.view = Gtk.TreeView(model=self.rows)
        for index, title in ((0, key_title), (1, value_title)):
            renderer = Gtk.CellRendererText()
            renderer.set_property("editable", True)
            renderer.connect("edited", self._edited, index)
            column = Gtk.TreeViewColumn(title, renderer, text=index)
            column.set_expand(True)
            self.view.append_column(column)
        scroller = Gtk.ScrolledWindow()
        scroller.add(self.view)
        self.box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        self.box.pack_start(scroller, True, True, 0)
        form = Gtk.Box(spacing=8)
        self.key_entry = Gtk.Entry()
        self.key_entry.set_placeholder_text(key_title)
        self.value_entry = Gtk.Entry()
        self.value_entry.set_placeholder_text(value_title + (" (\\n for a new line)" if multiline_value else ""))
        self.multiline_value = multiline_value
        add = Gtk.Button(label="Add")
        add.connect("clicked", lambda _button: self.add())
        self.value_entry.connect("activate", lambda _entry: self.add())
        remove = Gtk.Button(label="Remove")
        remove.connect("clicked", lambda _button: self.remove_selected())
        for widget, expand in ((self.key_entry, True), (self.value_entry, True), (add, False), (remove, False)):
            form.pack_start(widget, expand, expand, 0)
        self.box.pack_start(form, False, False, 0)

    def set_pairs(self, pairs: list[tuple[str, str]]) -> None:
        self.rows.clear()
        for key, value in pairs:
            self.rows.append([key, value])

    def pairs(self) -> list[tuple[str, str]]:
        return [(row[0], row[1]) for row in self.rows]

    def add(self) -> None:
        key, value = self.key_entry.get_text().strip(), self.value_entry.get_text()
        if self.multiline_value:
            value = value.replace("\\n", "\n")
        if not value.strip():
            return
        self.rows.append([key, value.strip()])
        self.key_entry.set_text("")
        self.value_entry.set_text("")
        self.on_changed()

    def remove_selected(self) -> None:
        _model, row = self.view.get_selection().get_selected()
        if row is not None:
            self.rows.remove(row)
            self.on_changed()

    def _edited(self, _renderer, path: str, new_text: str, column: int) -> None:
        self.rows[path][column] = new_text
        self.on_changed()
