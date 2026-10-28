"""The agent lane: "make / fix / run…" handed to a Codex thread through the `openclicky` CLI
(`agent/`), as the Mac's `OpenClickyAgentClient.swift` does.

- `classify_lane(question)`: `openclicky do <question> --gate-only --events` → "ask" or "agent".
  The CLI asks a cheap gate model and falls back to its own heuristic without one; a CLI that
  cannot run at all leaves the question to the ask lane.
- `run_agent(task, screenshot_path, thread_id, on_event)`: `openclicky run <task> --events --cwd
  <workspace>` with the screenshot attached and the last thread resumed. Milestones arrive as they
  happen (`on_event`), and the result has the final message, the thread and any files it saved.
- `cancel()`: a new question or esc interrupts the run (SIGINT, then SIGTERM after 2 s).

The CLI reads the same shell.json; own keys reach it as `OPENCLICKY_OPENAI_KEY` /
`OPENCLICKY_ANTHROPIC_KEY` (never `OPENAI_API_KEY`, which Codex would use behind its back), and the
account as `OPENCLICKY_TOKEN`. Work happens in `~/OpenClicky` (shell.json `workspace`).
"""

from __future__ import annotations

import json
import os
import shutil
import signal
import subprocess
import threading
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable

from . import applog
from .config import LinuxSettings, load_settings, openclicky_directory

TOOL_MISSING = ("agents run through the openclicky command-line tool, which isn't on this computer. "
                "questions, pointing and quick actions still work.")
GATE_TIMEOUT_SECONDS = 30


class AgentError(Exception):
    """Carries the sentence to show."""


@dataclass
class AgentRunResult:
    lane: str | None = None
    text: str = ""
    artifacts: list[str] = field(default_factory=list)
    thread_id: str | None = None
    status: str = "unknown"
    error_message: str | None = None


def _raw_shell_json() -> dict:
    try:
        value = json.loads((openclicky_directory() / "shell.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}
    return value if isinstance(value, dict) else {}


def cli_command(raw: dict | None = None, environment: dict[str, str] | None = None) -> list[str]:
    """`OPENCLICKY_SHELL_CLI`, else shell.json's `cliCommand` (the Mac's key), else `openclicky`."""
    environment = os.environ if environment is None else environment
    if environment.get("OPENCLICKY_SHELL_CLI"):
        return environment["OPENCLICKY_SHELL_CLI"].split()
    raw = _raw_shell_json() if raw is None else raw
    command = raw.get("cliCommand")
    if isinstance(command, list) and command and all(isinstance(part, str) for part in command):
        return [str(Path(part).expanduser()) if part.startswith("~") else part for part in command]
    return ["openclicky"]


def workspace_path(raw: dict | None = None, environment: dict[str, str] | None = None) -> Path:
    environment = os.environ if environment is None else environment
    raw = _raw_shell_json() if raw is None else raw
    workspace = environment.get("OPENCLICKY_WORKSPACE") or (raw.get("workspace") if isinstance(raw.get("workspace"), str) else None)
    return Path(workspace or "~/OpenClicky").expanduser()


def cli_environment(settings: LinuxSettings, raw: dict | None = None, base: dict[str, str] | None = None) -> dict[str, str]:
    """The Mac's `cliProcessEnvironment`: the backend, the account, own keys under OpenClicky's names,
    the workspace, and the usual places the CLI, codex and node live on a PATH a desktop may not set."""
    raw = _raw_shell_json() if raw is None else raw
    environment = dict(os.environ if base is None else base)
    environment["BACKEND_URL"] = settings.backend_url
    if settings.account_token:
        environment["OPENCLICKY_TOKEN"] = settings.account_token
    if isinstance(raw.get("model"), str) and raw["model"].strip():
        environment["OPENCLICKY_MODEL"] = raw["model"].strip()
    environment["OPENCLICKY_WORKSPACE"] = str(workspace_path(raw, environment))
    if settings.openai_api_key:
        environment["OPENCLICKY_OPENAI_KEY"] = settings.openai_api_key
    if settings.anthropic_api_key:
        environment["OPENCLICKY_ANTHROPIC_KEY"] = settings.anthropic_api_key
    environment.pop("OPENAI_API_KEY", None)
    environment.pop("ANTHROPIC_API_KEY", None)
    home = str(Path.home())
    extra = [f"{home}/.local/bin", f"{home}/.npm-global/bin", "/usr/local/bin"]
    environment["PATH"] = ":".join(extra + [environment.get("PATH") or "/usr/bin:/bin"])
    return environment


def is_tool_installed(settings: LinuxSettings | None = None) -> bool:
    command = cli_command()
    environment = cli_environment(settings or load_settings())
    return bool(command) and shutil.which(command[0], path=environment["PATH"]) is not None


def parse_events(lines: list[str], on_event: Callable[[str], None] = lambda _line: None) -> AgentRunResult:
    """The CLI's `--events` JSON Lines (lane / event / delta / answer / result / error), the Mac's
    `EventCollector`: anything that is not a JSON event is the streamed answer itself."""
    result = AgentRunResult()
    streamed = ""
    for line in lines:
        try:
            event = json.loads(line)
        except ValueError:
            event = None
        if not isinstance(event, dict) or not isinstance(event.get("type"), str):
            if line.strip():
                streamed += line + "\n"
            continue
        kind = event["type"]
        if kind == "lane":
            result.lane = event.get("lane")
        elif kind == "event" and isinstance(event.get("line"), str):
            on_event(event["line"])
        elif kind == "delta":
            streamed += event.get("text") or ""
        elif kind == "answer":
            result.text = event.get("text") or streamed
            result.status = "completed"
        elif kind == "result":
            result.thread_id = event.get("threadId")
            result.artifacts = [artifact for artifact in event.get("artifacts") or [] if isinstance(artifact, str)]
            result.status = event.get("status") or "completed"
            result.error_message = event.get("error")
            result.text = event.get("finalMessage") or streamed
        elif kind == "error":
            result.error_message = event.get("message") or "unknown error"
    if not result.text:
        result.text = streamed
    return result


def spoken_result(result: AgentRunResult) -> str:
    """What is said when a run ends (the Mac's `performAgentTask`)."""
    text = result.text.strip()
    if not text:
        text = "done." if result.status == "completed" else f"the agent stopped early: {result.error_message or result.status}"
    if result.artifacts:
        count = len(result.artifacts)
        text += f" i saved {count} {'file' if count == 1 else 'files'}."
    return text


class AgentClient:
    def __init__(self, settings_reader: Callable[[], LinuxSettings] = load_settings) -> None:
        self._settings_reader = settings_reader
        self._running: subprocess.Popen | None = None
        self._lock = threading.Lock()

    def _spawn(self, arguments: list[str]) -> subprocess.Popen:
        settings = self._settings_reader()
        raw = _raw_shell_json()
        command = cli_command(raw)
        environment = cli_environment(settings, raw)
        if shutil.which(command[0], path=environment["PATH"]) is None:
            raise AgentError(TOOL_MISSING)
        workspace = workspace_path(raw, environment)
        workspace.mkdir(parents=True, exist_ok=True)
        return subprocess.Popen(command + arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                                env=environment, cwd=workspace, start_new_session=True)

    def _execute(self, arguments: list[str], on_event: Callable[[str], None], timeout: float | None = None) -> AgentRunResult:
        process = self._spawn(arguments)
        with self._lock:
            self._running = process
        stderr_lines: list[str] = []

        def read_stderr() -> None:
            assert process.stderr is not None
            for line in process.stderr:
                line = line.strip()
                if line:
                    stderr_lines.append(line)
                    on_event(line.replace("▸ ", ""))
        reader = threading.Thread(target=read_stderr, daemon=True)
        reader.start()
        stdout_lines: list[str] = []
        assert process.stdout is not None
        timer = threading.Timer(timeout, self.cancel) if timeout else None
        if timer:
            timer.start()
        try:
            for line in process.stdout:
                stdout_lines.append(line.rstrip("\n"))
                # Milestones show as they come; the rest is read at the end.
                try:
                    event = json.loads(line)
                except ValueError:
                    continue
                if isinstance(event, dict) and event.get("type") == "event" and isinstance(event.get("line"), str):
                    on_event(event["line"])
            process.wait()
        finally:
            if timer:
                timer.cancel()
            with self._lock:
                if self._running is process:
                    self._running = None
        reader.join(timeout=1)
        result = parse_events(stdout_lines)
        if process.returncode != 0 and result.error_message is None:
            result.error_message = stderr_lines[-1] if stderr_lines else f"openclicky exited with status {process.returncode}"
        if process.returncode != 0:
            raise AgentError(result.error_message or "the agent stopped")
        return result

    def classify_lane(self, question: str) -> str:
        """"ask" or "agent"; "ask" when the CLI cannot run (the ask lane answers then)."""
        try:
            return self._execute(["do", question, "--gate-only", "--events"], lambda _line: None, timeout=GATE_TIMEOUT_SECONDS).lane or "agent"
        except (AgentError, OSError) as failure:
            applog.append(f"agent: the gate failed ({type(failure).__name__}); the ask lane answers")
            return "ask"

    def run_agent(self, task: str, screenshot_path: str | None, thread_id: str | None,
                  on_event: Callable[[str], None] = lambda _line: None) -> AgentRunResult:
        raw = _raw_shell_json()
        arguments = ["run", task, "--events", "--cwd", str(workspace_path(raw))]
        if screenshot_path:
            arguments += ["--image", screenshot_path]
        if thread_id:
            arguments += ["--thread", thread_id]
        if isinstance(raw.get("model"), str) and raw["model"].strip():
            arguments += ["--model", raw["model"].strip()]
        return self._execute(arguments, on_event)

    def cancel(self) -> None:
        with self._lock:
            process = self._running
        if process is None or process.poll() is not None:
            return
        try:
            os.killpg(process.pid, signal.SIGINT)
        except OSError:
            return

        def terminate_if_still_running() -> None:
            if process.poll() is None:
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except OSError:
                    pass
        threading.Timer(2.0, terminate_if_still_running).start()
