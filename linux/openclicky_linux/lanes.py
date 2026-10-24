"""Which lane a question goes down, the Mac's two-tier routing (`CompanionManager`'s ask lane):

- **the agent lane** runs when agent mode is on (Settings), there is a key or an account, and the
  `openclicky` CLI is installed: its gate decides, and "make a folder of my screenshots and zip it"
  goes to a Codex thread (resumed from one question to the next), its milestones shown beside the
  pointer while it works, its last message the answer;
- **the ask lane** answers everything else: the question and the screenshots to the model, which may
  point. Where the agent lane does not run, the quick actions (`actions.py`) are offered as tools,
  and what each did is said after the answer.

`answer` runs off the main thread (the ask controller's background work), so everything it reports
on the way (`progress`) is handed back to the main loop by the caller.
"""

from __future__ import annotations

import os
import re
import tempfile
from typing import Callable

from . import actions, applog, preferences
from .agent import AgentClient, is_tool_installed, spoken_result, workspace_path
from .ask import Conversation, Screenshot, as_screen_list, ask_with_tools
from .config import LinuxSettings, load_settings

AGENT_MODE = "agentMode"
# The markers a reply may end with ([POINT:…] trailing, [NEXT], [UNSURE]): what a quick action says
# goes before them, so they stay at the end where they are looked for.
_TRAILING_TAGS = re.compile(r"(\s*\[(?:POINT|NEXT|UNSURE)[^\]]*\])+\s*$")


def agent_mode_is_on() -> bool:
    return preferences.get(AGENT_MODE, True) is not False


def with_outcomes(answer: str, outcomes: list[str]) -> str:
    """The answer, then what each quick action did (the Mac's `spokenReply`), before any trailing tag."""
    outcomes = [outcome for outcome in outcomes if outcome]
    if not outcomes:
        return answer
    trailing = _TRAILING_TAGS.search(answer)
    body, tags = (answer[:trailing.start()], answer[trailing.start():]) if trailing else (answer, "")
    return " ".join(part for part in [body.strip(), *outcomes] if part) + tags


class Lanes:
    def __init__(self, agent: AgentClient | None = None, settings_reader: Callable[[], LinuxSettings] = load_settings,
                 progress: Callable[[str], None] = lambda _text: None, desktop: Callable[[], actions.Desktop] | None = None,
                 ask_model=ask_with_tools, tool_installed: Callable[[LinuxSettings], bool] = is_tool_installed,
                 agent_mode: Callable[[], bool] = agent_mode_is_on) -> None:
        self.agent = agent or AgentClient(settings_reader)
        self._settings_reader = settings_reader
        self.progress = progress
        self._desktop = desktop or (lambda: actions.live_desktop(workspace_path()))
        self._ask_model = ask_model
        self._tool_installed = tool_installed
        self._agent_mode = agent_mode
        # The Codex thread the last task ran on: the next one continues it ("now zip it").
        self.last_thread_id: str | None = None
        # Every lane taken, in order (the tests and the log read it).
        self.lanes_taken: list[str] = []

    def agent_lane_runs(self, settings: LinuxSettings) -> bool:
        has_a_way_to_answer = bool(settings.anthropic_api_key or settings.openai_api_key or settings.openrouter_api_key or settings.account_token)
        return self._agent_mode() and has_a_way_to_answer and self._tool_installed(settings)

    def answer(self, question: str, screenshot: "Screenshot | list[Screenshot]", conversation: Conversation,
               progress: Callable[[str], None] | None = None) -> str:
        """`progress` (this question's own, else the one given at start) hears what the agent is doing."""
        progress = progress or self.progress
        settings = self._settings_reader()
        agent_lane_runs = self.agent_lane_runs(settings)
        if agent_lane_runs:
            progress("deciding…")
            if self.agent.classify_lane(question) == "agent":
                self.lanes_taken.append("agent")
                return self._run_agent(question, as_screen_list(screenshot), progress)
        self.lanes_taken.append("ask")
        tools = None if agent_lane_runs else actions.tool_definitions()
        text, calls = self._ask_model(question, screenshot, conversation, settings, tools=tools,
                                      extra_instructions=actions.TOOLS_INSTRUCTION if tools else "")
        if not calls:
            return text
        desktop = self._desktop()
        outcomes = []
        for call in calls:
            outcome = actions.perform(call.name, call.arguments, desktop)
            applog.append(f"quick action: {call.name} {'did it' if outcome.did_something else 'did nothing'}")
            outcomes.append(outcome.sentence)
        return with_outcomes(text, outcomes)

    def _run_agent(self, task: str, screens: list[Screenshot], progress: Callable[[str], None]) -> str:
        progress("starting agent…")
        screenshot_path = None
        if screens:
            # The monitor the question was about (the first) rides along as image context.
            descriptor, screenshot_path = tempfile.mkstemp(prefix="openclicky-", suffix=".png")
            with os.fdopen(descriptor, "wb") as screenshot_file:
                screenshot_file.write(screens[0].png_bytes)
        try:
            result = self.agent.run_agent(task, screenshot_path, self.last_thread_id, on_event=progress)
        finally:
            if screenshot_path:
                os.unlink(screenshot_path)
        if result.thread_id:
            self.last_thread_id = result.thread_id
        applog.append(f"agent: task finished ({result.status}, {len(result.artifacts)} file(s))")
        # Never a [POINT] tag: the agent's words are its own, not a place on screen.
        return spoken_result(result).replace("[POINT", "(POINT")

    def cancel(self) -> None:
        self.agent.cancel()
