"""The Words window: the Mac window's "Your words" and "How it writes" pages, in three tabs.

- **History**: this week's counts, every take newest first with the app and the time, a search
  over what was said and written; a take's text can be copied, edited (the old text kept as a
  revision) or deleted, and the whole history cleared. Nothing in it leaves the computer.
- **Dictionary**: names spelled your way ("you say aditya shatriya → it writes Aaditya Kshatriya").
- **Shortcuts**: say exactly the trigger, get the saved text.

Both lists are the Mac's own files under `~/.openclicky/dictation` (`space.save_words`).
"""

from __future__ import annotations

from datetime import datetime
from typing import Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, Gtk  # noqa: E402

from .history import TakeStore  # noqa: E402
from .space import DictionaryTerm, SpokenShortcut, load_space, save_words  # noqa: E402

HISTORY_LIMIT = 300


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
    def __init__(self, take_store: TakeStore | None, on_closed: Callable[[], None] = lambda: None) -> None:
        self.take_store = take_store
        self.window = Gtk.Window(title="OpenClicky — your words")
        self.window.set_default_size(680, 560)
        self.window.set_border_width(12)
        self.window.connect("delete-event", lambda *_: self.window.hide() or on_closed() or True)
        self.notebook = Gtk.Notebook()
        self.window.add(self.notebook)
        self.notebook.append_page(self._history_page(), Gtk.Label(label="History"))
        self.dictionary_editor = PairEditor("you say", "it writes", self._save_words)
        self.notebook.append_page(self._page(self.dictionary_editor.box,
                                             "Names and words spelled your way. Separate several ways it is heard with commas."),
                                  Gtk.Label(label="Dictionary"))
        self.shortcuts_editor = PairEditor("you say exactly", "it writes", self._save_words, multiline_value=True)
        self.notebook.append_page(self._page(self.shortcuts_editor.box, "Say the whole trigger on its own and the saved text goes in."),
                                  Gtk.Label(label="Shortcuts"))

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
        top = Gtk.Box(spacing=8)
        self.search = Gtk.SearchEntry()
        self.search.set_placeholder_text("search what you said")
        self.search.connect("search-changed", lambda _entry: self.show_history())
        top.pack_start(self.search, True, True, 0)
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
        for label, action in (("Copy", self._copy_selected), ("Delete", self._delete_selected)):
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
        for take in self.take_store.recent(limit=HISTORY_LIMIT, query=self.search.get_text() or None):
            if take.status != "complete":
                continue
            self.history_rows.append([take.id, when(take.created_at), take.app_name or "", take.display_text])

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
        self.history_rows.remove(row)
        self.history_status.set_text("deleted.")

    def _edited(self, _renderer, path: str, new_text: str) -> None:
        if self.take_store is None or not new_text.strip():
            return
        take_id = self.history_rows[path][0]
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

    def show(self, page: int = 0) -> None:
        self.show_history()
        self._load_words()
        self.window.show_all()
        self.notebook.set_current_page(page)
        self.window.present()


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
