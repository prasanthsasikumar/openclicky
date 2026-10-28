"""The island (the Mac's `NotchHUD`): a small black shape at the top centre of the screen, under the
desktop's top bar, or a drawer on the left or right edge (Settings → General → Where OpenClicky
lives; "hidden" shows nothing until a shortcut is used).

- **collapsed**: a thin handle;
- **peek**: hovering shows the pointer's status (or a live coding session, or the track playing)
  and "click to open";
- **busy**: while OpenClicky listens, thinks, speaks, guides or an agent works, a strip says which,
  with a stop button (esc's job);
- **open**: a click shows the band (the pointer's status, the voice on or off, dock the pointer,
  Settings, quit) and under it one component at a time: Shortcuts, Transcript (the last take),
  Agent, and every plugin switched on (`island_plugins.py`), in the order Settings gives.

The black melts into a see-through glass below it, as far as Settings → General → Glass says (0:
all black). On Wayland the island is an XWayland window like the rest of OpenClicky's.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass
from typing import Any, Callable

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("Pango", "1.0")
from gi.repository import Gdk, GLib, Gtk, Pango  # noqa: E402

from . import island_plugins, preferences  # noqa: E402

PLACEMENTS = (("notch", "Top of the screen"), ("left", "Left edge"), ("right", "Right edge"), ("hidden", "Hidden"))
PLACEMENT_DETAILS = {"notch": "Hangs from the top of the screen. Hover to peek, click to open.",
                     "left": "A drawer on the left edge. Hover the handle to open.",
                     "right": "A drawer on the right edge. Hover the handle to open.",
                     "hidden": "Nothing on screen until you use a shortcut. The tray icon opens the panel."}
SHORTCUTS, TRANSCRIPT, AGENT = "shortcuts", "transcript", "agent"
BUILT_IN_TITLES = {SHORTCUTS: "Shortcuts", TRANSCRIPT: "Transcript", AGENT: "Agent"}
PLUGINS_KEY = "islandPlugins"  # the plugins switched on (preferences)
ORDER_KEY = "islandComponentOrder"
DEFAULT_COMPONENT_KEY = "islandDefaultComponent"
DOCKED_KEY = "pointerDocked"
TICK_MILLISECONDS = 400
# How long a transcript stays the moment's component after it landed (the Mac's 8 s).
LANDED_TRANSCRIPT_SECONDS = 8.0
HANDLE_SIZE = (150, 7)
TOP_GAP = 2
OPEN_REDRAW_SECONDS = 2.0


@dataclass(frozen=True)
class Moment:
    """What is happening, as the island reads it every tick."""
    dictating: bool = False
    dictation_working: bool = False
    asking_status: str | None = None  # "sending your screen · esc to cancel", a guide's step…
    answering: bool = False
    speaking: bool = False
    agent_activity: str | None = None
    last_take: str | None = None
    last_take_at: float = 0.0
    voice_name: str | None = None
    # Fast voice: its state ("listening", "thinking", "speaking", or "idle") and hands-free.
    fast_voice_state: str | None = None
    hands_free: bool = False


def busy_text(moment: Moment) -> str | None:
    """The busy strip's words (the Mac's `statusText`), or None when nothing is going on."""
    if moment.asking_status:
        return moment.asking_status
    if moment.fast_voice_state in ("listening", "connecting"):
        return "listening · hands-free" if moment.hands_free else "listening"
    if moment.fast_voice_state == "thinking":
        return "looking at your screen"
    if moment.fast_voice_state == "speaking":
        return "speaking · fast voice"
    if moment.dictating:
        return "dictating"
    if moment.dictation_working:
        return "typing it in"
    if moment.answering:
        return "working" if moment.agent_activity else "looking at your screen"
    if moment.speaking:
        return f"speaking · {moment.voice_name}" if moment.voice_name else "speaking"
    if moment.agent_activity:
        return "an agent is working"
    if moment.hands_free:
        return "hands-free · just talk"
    return None


def pointer_status(presence: str, docked: bool, short: bool = False) -> str:
    """The pointer's status in the band (and, shorter, on the peek line)."""
    if docked:
        return "pointer docked" if short else "pointer is docked"
    return {"always": ("pointer follows you", "pointer follows your mouse"),
            "whileMoving": ("pointer while moving", "pointer follows while moving"),
            "onShake": ("shake for the pointer", "shake the mouse for the pointer"),
            "whenAnswering": ("pointer while answering", "pointer shows while answering")}.get(presence, ("pointer", "pointer"))[0 if short else 1]


def enabled_plugins() -> list[str]:
    value = preferences.get(PLUGINS_KEY, [])
    return [plugin for plugin in value if plugin in island_plugins.PLUGIN_TITLES] if isinstance(value, list) else []


def set_plugin_enabled(plugin: str, enabled: bool) -> None:
    plugins = [name for name in enabled_plugins() if name != plugin] + ([plugin] if enabled else [])
    preferences.set_values(**{PLUGINS_KEY: plugins or None})


def components() -> list[str]:
    """The components the capsule offers, in the person's order: the built-ins, then plugins on."""
    available = [SHORTCUTS, TRANSCRIPT, AGENT, *enabled_plugins()]
    order = preferences.get(ORDER_KEY, [])
    ordered = [name for name in order if name in available] if isinstance(order, list) else []
    return ordered + [name for name in available if name not in ordered]


def component_for(moment: Moment, available: list[str], chosen: str | None, now: float) -> str:
    """The person's pick, else the moment's (a take landing, an agent at work, a track playing), else
    the default, else the first."""
    if chosen in available:
        return chosen
    if moment.dictating or (moment.last_take and now - moment.last_take_at < LANDED_TRANSCRIPT_SECONDS):
        return TRANSCRIPT
    if moment.agent_activity:
        return AGENT
    default = preferences.get(DEFAULT_COMPONENT_KEY)
    return default if default in available else available[0]


def glass_css(glass_level: float) -> bytes:
    """Black at the top melting into glass below, as far as `glass_level` (0: all black)."""
    bottom_alpha = 1.0 - 0.55 * max(0.0, min(1.0, glass_level))
    return (".openclicky-island { background-image: linear-gradient(to bottom, rgba(0,0,0,1) 0%, rgba(0,0,0,1) 30%, "
            f"rgba(16,16,18,{bottom_alpha:.2f}) 100%); background-color: transparent; border-radius: 0 0 16px 16px; color: #f4f1ec; }}"
            ".openclicky-island.side-left { border-radius: 0 16px 16px 0; } .openclicky-island.side-right { border-radius: 16px 0 0 16px; }"
            ".openclicky-island label { color: #f4f1ec; } .openclicky-island .dim { color: alpha(#f4f1ec, 0.55); }"
            ".openclicky-island button { color: #f4f1ec; background: alpha(#ffffff, 0.08); border: none; box-shadow: none;"
            " padding: 2px 8px; min-height: 22px; } .openclicky-island button:hover { background: alpha(#ffffff, 0.18); }"
            ".openclicky-island button.chosen { background: alpha(#f0452b, 0.55); }"
            ".openclicky-island-handle { background-color: rgba(0,0,0,0.85); border-radius: 0 0 4px 4px; }"
            ".openclicky-island .busy-dot { color: #f0452b; }").encode()


@dataclass
class IslandHooks:
    """What the island can do and read (the app joins these up in `__main__.py`)."""
    moment: Callable[[], Moment]
    stop: Callable[[], None]
    open_settings: Callable[[], None]
    quit: Callable[[], None]
    pointer_presence: Callable[[], str]
    docked: Callable[[], bool]
    set_docked: Callable[[bool], None]
    speak_answers: Callable[[], bool]
    set_speak_answers: Callable[[bool], None]
    shortcut_rows: Callable[[], list]
    copy_text: Callable[[str], None]
    follow_up_agent: Callable[[], None]


class Island:
    def __init__(self, hooks: IslandHooks, placement: str = "notch", glass_level: float = 0.6) -> None:
        self.hooks = hooks
        self.placement = placement
        self.state = "collapsed"  # collapsed | peek | busy | open
        self.chosen_component: str | None = None
        self._leave_timer: int | None = None
        self._css = Gtk.CssProvider()
        Gtk.StyleContext.add_provider_for_screen(Gdk.Screen.get_default(), self._css, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
        self.set_glass(glass_level)
        self.window = Gtk.Window(type=Gtk.WindowType.POPUP)
        self.window.set_keep_above(True)
        self.window.set_accept_focus(False)
        screen = self.window.get_screen()
        if screen.is_composited() and screen.get_rgba_visual() is not None:
            self.window.set_visual(screen.get_rgba_visual())
            self.window.set_app_paintable(True)
        self.window.add_events(Gdk.EventMask.ENTER_NOTIFY_MASK | Gdk.EventMask.LEAVE_NOTIFY_MASK | Gdk.EventMask.BUTTON_PRESS_MASK
                               | Gdk.EventMask.POINTER_MOTION_MASK)
        self.window.connect("motion-notify-event", self._moved_inside)
        # When the pointer last moved over the island (Wayland: see `mouse_moved_somewhere`).
        self._last_inside_motion = 0.0
        self.window.connect("enter-notify-event", self._entered)
        self.window.connect("leave-notify-event", self._left)
        self.window.connect("button-press-event", self._clicked)
        self.body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.window.add(self.body)
        self.media = island_plugins.MediaPlayers()
        self.system = island_plugins.SystemSampler()
        self.shelf = island_plugins.Shelf()
        self._usage: tuple | None = None
        self._usage_fetched_at = 0.0
        self._sessions: list = []
        self._sessions_read_at = 0.0
        self._track = None
        self._last_drawn: tuple | None = None
        self._drawn_at = 0.0
        self._show()
        GLib.timeout_add(TICK_MILLISECONDS, self._tick)

    # MARK: settings

    def set_glass(self, glass_level: float) -> None:
        self._css.load_from_data(glass_css(glass_level))

    def set_placement(self, placement: str) -> None:
        self.placement = placement
        self.state = "collapsed"
        self._show(force=True)

    # MARK: the states

    def _entered(self, _window, event) -> bool:
        if event.detail == Gdk.NotifyType.INFERIOR:
            return False
        self._moved_inside()
        self._cancel_leave()
        if self.state in ("collapsed",):
            # A side drawer opens on hover; the top island only peeks.
            self.state = "open" if self.placement in ("left", "right") else "peek"
            self._show(force=True)
        return False

    def _inside(self, event) -> bool:
        """The pointer is still over the island (crossing from one of its buttons to another)."""
        x, y = self.window.get_position()
        width, height = self.window.get_size()
        return x <= event.x_root < x + width and y <= event.y_root < y + height

    def _left(self, _widget, event) -> bool:
        if event.detail == Gdk.NotifyType.INFERIOR or self._inside(event):
            return False
        self._cancel_leave()
        self._leave_timer = GLib.timeout_add(700 if self.state == "open" else 250, self._collapse_after_leaving)
        return False

    def _moved_inside(self, *_arguments) -> bool:
        self._last_inside_motion = time.monotonic()
        return False

    def mouse_moved_somewhere(self) -> None:
        """Wayland: the mouse moved (the input devices say so, never where). XWayland sends no leave
        once the panel was drawn again under the pointer, so a movement the island did not see
        itself means the pointer is elsewhere: it folds, as on leaving."""
        if self.state not in ("peek", "open"):
            return
        moved_at = time.monotonic()

        def check() -> bool:
            if self._last_inside_motion < moved_at - 0.05 and self.state in ("peek", "open") and self._leave_timer is None:
                self._leave_timer = GLib.timeout_add(700 if self.state == "open" else 250, self._collapse_after_leaving)
            return False

        GLib.timeout_add(250, check)

    def _cancel_leave(self) -> None:
        if self._leave_timer is not None:
            GLib.source_remove(self._leave_timer)
            self._leave_timer = None

    def _collapse_after_leaving(self) -> bool:
        self._leave_timer = None
        if self.state in ("peek", "open"):
            self.state = "collapsed"
            self.chosen_component = None
            self._show(force=True)
        return False

    def _clicked(self, _window, event) -> bool:
        if event.button == 1 and self.state in ("collapsed", "peek"):
            self.state = "open"
            self._show(force=True)
        return False

    def open(self) -> None:
        """The tray's "Open the island"."""
        self.state = "open"
        self._show(force=True)

    def _tick(self) -> bool:
        moment = self.hooks.moment()
        busy = busy_text(moment)
        if busy and self.state == "collapsed":
            self.state = "busy"
        elif not busy and self.state == "busy":
            self.state = "collapsed"
        self._show()
        return True

    # MARK: drawing

    def _show(self, force: bool = False) -> None:
        if self.placement == "hidden" and self.state != "open":
            self.window.hide()
            return
        moment = self.hooks.moment()
        drawn = (self.state, self.placement, busy_text(moment), self.chosen_component, moment.last_take, moment.agent_activity)
        # The open panel's plugins move on their own (the figures, the track): drawn again every few seconds.
        stale = self.state == "open" and time.monotonic() - self._drawn_at > OPEN_REDRAW_SECONDS
        if not force and drawn == self._last_drawn and not stale:
            return
        self._last_drawn = drawn
        self._drawn_at = time.monotonic()
        for child in self.body.get_children():
            self.body.remove(child)
        style = self.body.get_style_context()
        for name in ("openclicky-island", "openclicky-island-handle", "side-left", "side-right"):
            style.remove_class(name)
        if self.state == "collapsed":
            style.add_class("openclicky-island-handle")
            handle = Gtk.Box()
            width, height = HANDLE_SIZE if self.placement == "notch" else (HANDLE_SIZE[1], HANDLE_SIZE[0] // 2)
            handle.set_size_request(width, height)
            self.body.pack_start(handle, False, False, 0)
        else:
            style.add_class("openclicky-island")
            if self.placement in ("left", "right"):
                style.add_class(f"side-{self.placement}")
            self.body.set_border_width(10)
            if self.state == "peek":
                self.body.pack_start(self._peek_line(), False, False, 0)
            elif self.state == "busy":
                self.body.pack_start(self._busy_strip(moment), False, False, 0)
            else:
                self._open_panel(moment)
        if self.state == "collapsed":
            self.body.set_border_width(0)
        self.window.resize(1, 1)
        self.window.show_all()
        self._place()
        # Leaving from over a button, GTK hands the crossing to the button, not the window.
        for button in _buttons_in(self.body):
            button.connect("leave-notify-event", self._left)
            button.connect("motion-notify-event", self._moved_inside)

    def _place(self) -> None:
        display = Gdk.Display.get_default()
        area = (display.get_primary_monitor() or display.get_monitor(0)).get_workarea()
        # The size it is about to have (a resize has not reached the window yet).
        natural = self.window.get_preferred_size()[1]
        width, height = natural.width, natural.height
        if self.placement == "left":
            self.window.move(area.x, area.y + (area.height - height) // 2)
        elif self.placement == "right":
            self.window.move(area.x + area.width - width, area.y + (area.height - height) // 2)
        else:
            self.window.move(area.x + (area.width - width) // 2, area.y + TOP_GAP)

    @staticmethod
    def _label(text: str, dim: bool = False, wrap: bool = False) -> Gtk.Label:
        label = Gtk.Label(label=text, xalign=0)
        if dim:
            label.get_style_context().add_class("dim")
        if wrap:
            label.set_line_wrap(True)
            label.set_max_width_chars(52)
        return label

    def _peek_line(self) -> Gtk.Widget:
        now = time.time()
        plugins = enabled_plugins()
        agent_line = island_plugins.peek_line(self._agent_sessions(now), now) if island_plugins.AGENTS in plugins else None
        media_line = self._track.line if island_plugins.MEDIA in plugins and self._track and self._track.playing else None
        status = agent_line or media_line or pointer_status(self.hooks.pointer_presence(), self.hooks.docked(), short=True)
        row = Gtk.Box(spacing=6)
        line = self._label(f"◤ {status}")
        line.set_ellipsize(Pango.EllipsizeMode.END)
        # An ellipsized label asks for no width at all unless told how many characters to show.
        line.set_width_chars(min(len(status) + 2, 44))
        line.set_max_width_chars(44)
        row.pack_start(line, False, False, 0)
        row.pack_start(self._label("· click to open", dim=True), False, False, 0)
        return row

    def _busy_strip(self, moment: Moment) -> Gtk.Widget:
        row = Gtk.Box(spacing=10)
        dot = self._label("●")
        dot.get_style_context().add_class("busy-dot")
        row.pack_start(dot, False, False, 0)
        row.pack_start(self._label(busy_text(moment) or ""), True, True, 0)
        stop = Gtk.Button(label="■ stop")
        stop.connect("clicked", lambda _button: self.hooks.stop())
        row.pack_end(stop, False, False, 0)
        return row

    def _open_panel(self, moment: Moment) -> None:
        band = Gtk.Box(spacing=8)
        band.pack_start(self._label(f"◤ {pointer_status(self.hooks.pointer_presence(), self.hooks.docked())}"), True, True, 0)
        voice = Gtk.Button(label="voice on" if self.hooks.speak_answers() else "voice off")
        voice.connect("clicked", lambda _button: (self.hooks.set_speak_answers(not self.hooks.speak_answers()), self._show(force=True)))
        dock = Gtk.Button(label="release" if self.hooks.docked() else "dock")
        dock.set_tooltip_text("Keep the pointer here until an answer needs it")
        dock.connect("clicked", lambda _button: (self.hooks.set_docked(not self.hooks.docked()), self._show(force=True)))
        settings = Gtk.Button(label="⚙")
        settings.set_tooltip_text("Settings")
        settings.connect("clicked", lambda _button: self.hooks.open_settings())
        quit_button = Gtk.Button(label="quit")
        quit_button.set_tooltip_text("Quit OpenClicky")
        quit_button.connect("clicked", lambda _button: self.hooks.quit())
        for widget in (voice, dock, settings, quit_button):
            band.pack_start(widget, False, False, 0)
        self.body.pack_start(band, False, False, 0)
        busy = busy_text(moment)
        if busy:
            self.body.pack_start(self._busy_strip(moment), False, False, 0)
        available = components()
        chosen = component_for(moment, available, self.chosen_component, time.time())
        if len(available) > 1:
            capsule = Gtk.Box(spacing=4)
            for name in available:
                button = Gtk.Button(label=BUILT_IN_TITLES.get(name) or island_plugins.PLUGIN_TITLES[name])
                if name == chosen:
                    button.get_style_context().add_class("chosen")
                button.connect("clicked", lambda _button, name=name: self._choose(name))
                capsule.pack_start(button, False, False, 0)
            self.body.pack_start(capsule, False, False, 0)
        self.body.pack_start(self._component(chosen, moment), False, False, 0)

    def _choose(self, name: str) -> None:
        self.chosen_component = name
        self._show(force=True)

    # MARK: the components

    def _component(self, name: str, moment: Moment) -> Gtk.Widget:
        builder = {SHORTCUTS: self._shortcuts, TRANSCRIPT: self._transcript, AGENT: self._agent, island_plugins.SYSTEM: self._system,
                   island_plugins.MEDIA: self._media, island_plugins.AGENTS: self._coding_agents, island_plugins.SHELF: self._shelf}
        return builder[name](moment)

    def _shortcuts(self, _moment: Moment) -> Gtk.Widget:
        grid = Gtk.Grid(column_spacing=16, row_spacing=6)
        for index, (how, keys, what) in enumerate(self.hooks.shortcut_rows()[:4]):
            tile = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
            tile.pack_start(self._label(f"{how} {' + '.join(keys)}"), False, False, 0)
            tile.pack_start(self._label(what.split(":")[0].split(".")[0], dim=True), False, False, 0)
            grid.attach(tile, index % 2, index // 2, 1, 1)
        return grid

    def _transcript(self, moment: Moment) -> Gtk.Widget:
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        if moment.dictating:
            box.pack_start(self._label("listening…", dim=True), False, False, 0)
        if moment.last_take:
            box.pack_start(self._label(moment.last_take[:400], wrap=True), False, False, 0)
            copy = Gtk.Button(label="copy")
            copy.set_halign(Gtk.Align.START)
            copy.connect("clicked", lambda _button: self.hooks.copy_text(moment.last_take))
            box.pack_start(copy, False, False, 0)
        elif not moment.dictating:
            box.pack_start(self._label("your last take shows here.", dim=True), False, False, 0)
        return box

    def _agent(self, moment: Moment) -> Gtk.Widget:
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        box.pack_start(self._label(moment.agent_activity or "no agent at work. ask for a task (“make a folder of my screenshots”).",
                                   dim=not moment.agent_activity, wrap=True), False, False, 0)
        follow_up = Gtk.Button(label="give the agent a task")
        follow_up.set_halign(Gtk.Align.START)
        follow_up.connect("clicked", lambda _button: self.hooks.follow_up_agent())
        box.pack_start(follow_up, False, False, 0)
        return box

    def _system(self, _moment: Moment) -> Gtk.Widget:
        row = Gtk.Box(spacing=18)
        for name, fraction, figure in island_plugins.system_figures(self.system.sample()):
            tile = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2)
            ring = Gtk.DrawingArea()
            ring.set_size_request(30, 30)
            ring.connect("draw", _draw_ring, fraction, island_plugins.is_warning(name, fraction))
            tile.pack_start(ring, False, False, 0)
            tile.pack_start(self._label(name), False, False, 0)
            tile.pack_start(self._label(figure, dim=True), False, False, 0)
            row.pack_start(tile, False, False, 0)
        return row

    def _media(self, _moment: Moment) -> Gtk.Widget:
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        track = self._track
        if track is None:
            box.pack_start(self._label("nothing playing", dim=True), False, False, 0)
            return box
        box.pack_start(self._label(track.title), False, False, 0)
        if track.artist:
            box.pack_start(self._label(track.artist, dim=True), False, False, 0)
        controls = Gtk.Box(spacing=6)
        for label, method in (("⏮", "Previous"), ("⏸" if track.playing else "▶", "PlayPause"), ("⏭", "Next")):
            button = Gtk.Button(label=label)
            button.connect("clicked", lambda _button, method=method: self._media_command(track.player, method))
            controls.pack_start(button, False, False, 0)
        box.pack_start(controls, False, False, 0)
        return box

    def _media_command(self, player: str, method: str) -> None:
        def run() -> None:
            self.media.command(player, method)
            self._track = self.media.now_playing()
            GLib.idle_add(lambda: (self._show(force=True), False)[1])
        threading.Thread(target=run, daemon=True).start()

    def _agent_sessions(self, now: float) -> list:
        if now - self._sessions_read_at > 3:
            self._sessions_read_at = now
            self._sessions = island_plugins.agent_sessions(now=now)
        return self._sessions

    def _coding_agents(self, _moment: Moment) -> Gtk.Widget:
        now = time.time()
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        sessions = self._agent_sessions(now)
        for session in sessions[:3]:
            live = "●" if session.is_live(now) else "○"
            box.pack_start(self._label(f"{live} {session.agent} · {session.project} · {session.activity.line}"), False, False, 0)
        if len(sessions) > 3:
            box.pack_start(self._label(f"+{len(sessions) - 3} more", dim=True), False, False, 0)
        if not sessions:
            box.pack_start(self._label("no claude code or codex session in the last ten minutes", dim=True), False, False, 0)
        if self._usage:
            five, seven = self._usage
            resets = island_plugins.reset_text(five.resets_at)
            box.pack_start(self._label(f"claude plan: {five.fraction:.0%} of 5 h{f' ({resets})' if resets else ''}, "
                                       f"{seven.fraction:.0%} of the week", dim=True), False, False, 0)
        return box

    def _shelf(self, _moment: Moment) -> Gtk.Widget:
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
        items = self.shelf.items()
        if not items:
            box.pack_start(self._label("drop files, links or text here to keep them", dim=True), False, False, 0)
        row = Gtk.FlowBox()
        row.set_max_children_per_line(5)
        row.set_selection_mode(Gtk.SelectionMode.NONE)
        for item in items[:10]:
            tile = Gtk.Button(label=item.label)
            tile.set_tooltip_text(item.value)
            tile.drag_source_set(Gdk.ModifierType.BUTTON1_MASK, [], Gdk.DragAction.COPY)
            tile.drag_source_add_uri_targets() if item.kind == "file" else tile.drag_source_add_text_targets()
            tile.connect("drag-data-get", _give_shelf_item, item)
            tile.connect("drag-end", lambda _tile, _context, item=item: (self.shelf.remove(item), self._show(force=True)))
            tile.connect("clicked", lambda _button, item=item: self.hooks.copy_text(item.value))
            row.add(tile)
        box.pack_start(row, False, False, 0)
        return box

    def take_drops(self) -> None:
        """The whole island takes drops while the shelf is on: they go on the shelf."""
        self.window.drag_dest_set(Gtk.DestDefaults.ALL, [], Gdk.DragAction.COPY)
        self.window.drag_dest_add_uri_targets()
        self.window.drag_dest_add_text_targets()
        self.window.connect("drag-data-received", self._dropped)
        self.window.connect("drag-motion", lambda *_arguments: (self.open(), self._choose(island_plugins.SHELF), False)[-1]
                            if island_plugins.SHELF in enabled_plugins() else False)

    def _dropped(self, _window, _context, _x, _y, data, _info, _time) -> None:
        if island_plugins.SHELF not in enabled_plugins():
            return
        items = island_plugins.shelf_items_from_drop(data.get_uris(), data.get_text())
        if items:
            self.shelf.add(items)
            self._show(force=True)

    def refresh_slow_data(self) -> None:
        """Off the main loop: the track playing and Claude's limits, for the plugins that are on."""
        plugins = enabled_plugins()
        if island_plugins.MEDIA in plugins:
            self._track = self.media.now_playing()
        if island_plugins.AGENTS in plugins and time.time() - self._usage_fetched_at > island_plugins.USAGE_FETCH_SECONDS:
            self._usage_fetched_at = time.time()
            token = island_plugins.claude_access_token()
            self._usage = island_plugins.fetch_usage(token) if token else None


def _buttons_in(container: Gtk.Widget) -> list[Gtk.Widget]:
    found = []
    for child in container.get_children() if isinstance(container, Gtk.Container) else []:
        if isinstance(child, Gtk.Button):
            found.append(child)
        found += _buttons_in(child)
    return found


def _give_shelf_item(_tile, _context, data, _info, _time, item) -> None:
    if item.kind == "file":
        from pathlib import Path

        data.set_uris([Path(item.value).as_uri()])
    else:
        data.set_text(item.value, -1)


def _draw_ring(area, context, fraction: float, warning: bool) -> bool:
    import math

    size = min(area.get_allocated_width(), area.get_allocated_height())
    centre, radius = size / 2, size / 2 - 3
    context.set_line_width(3)
    context.set_source_rgba(1, 1, 1, 0.18)
    context.arc(centre, centre, radius, 0, 2 * math.pi)
    context.stroke()
    context.set_source_rgba(1.0, 0.72, 0.2, 1) if warning else context.set_source_rgba(0.94, 0.27, 0.17, 1)
    context.arc(centre, centre, radius, -math.pi / 2, -math.pi / 2 + 2 * math.pi * max(0.0, min(1.0, fraction)))
    context.stroke()
    return False


def start_slow_refresh(island: Island, seconds: float = 5.0) -> Any:
    """Every few seconds, off the main loop, the plugins' slower data."""
    def loop() -> None:
        while True:
            try:
                island.refresh_slow_data()
            except Exception:  # noqa: BLE001 - a plugin's hiccup never stops the island
                pass
            time.sleep(seconds)

    thread = threading.Thread(target=loop, daemon=True)
    thread.start()
    return thread
