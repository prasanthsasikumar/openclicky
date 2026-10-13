import sys

from openclicky_linux import applog, autostart


def test_the_log_is_written_with_times_and_trimmed_when_too_big(tmp_path, monkeypatch):
    monkeypatch.setattr(applog, "_path", None)  # put back after the test, so later tests log nowhere
    monkeypatch.setenv("XDG_STATE_HOME", str(tmp_path))
    path = applog.log_path()
    path.parent.mkdir(parents=True)
    path.write_bytes(b"old line\n" * (applog.MAXIMUM_BYTES // 9 + 1000))
    assert applog.start() == path
    applog.append("take 3 done: 12 chars, paste=typed")
    content = path.read_text()
    assert len(content.encode()) < applog.MAXIMUM_BYTES
    assert content.startswith("old line\n")
    assert content.splitlines()[-1].endswith(" take 3 done: 12 chars, paste=typed")
    assert "openclicky started" in content


def test_the_log_does_nothing_before_it_is_started(tmp_path, monkeypatch):
    monkeypatch.setattr(applog, "_path", None)
    monkeypatch.setenv("XDG_STATE_HOME", str(tmp_path))
    applog.append("nothing")
    assert not (tmp_path / "openclicky").exists()


def test_autostart_writes_and_removes_its_desktop_entry(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    assert not autostart.is_enabled()
    autostart.set_enabled(True)
    entry = (tmp_path / "autostart" / "openclicky.desktop").read_text()
    assert "Exec=" in entry and "Name=OpenClicky" in entry
    assert autostart.is_enabled()
    autostart.set_enabled(False)
    assert not autostart.is_enabled()
    autostart.set_enabled(False)  # already off: nothing to do, no error


def test_autostart_starts_this_copy(tmp_path, monkeypatch):
    # The test virtualenv has its own openclicky-linux beside its python (pip install -e).
    command = autostart.launch_command()
    assert "openclicky-linux" in command or "-m openclicky_linux" in command
    assert sys.executable.rsplit("/", 1)[0] in command or "-m openclicky_linux" in command


def test_a_log_folder_that_cannot_be_made_means_no_log_not_no_app(tmp_path, monkeypatch):
    monkeypatch.setattr(applog, "_path", None)
    blocker = tmp_path / "a file, not a folder"
    blocker.write_text("")
    monkeypatch.setenv("XDG_STATE_HOME", str(blocker))
    assert applog.start() is None
    applog.append("ignored")  # and logging later still never raises


def test_an_entry_switched_off_by_the_desktop_counts_as_off(tmp_path, monkeypatch):
    monkeypatch.setenv("XDG_CONFIG_HOME", str(tmp_path))
    autostart.set_enabled(True)
    path = autostart.autostart_path()
    path.write_text(path.read_text() + "Hidden=true\n")
    assert not autostart.is_enabled()
    path.write_text(autostart.desktop_entry("openclicky-linux").replace("enabled=true", "enabled=false"))
    assert not autostart.is_enabled()


def test_the_exec_line_is_quoted_the_desktop_entry_way():
    assert autostart._quoted("/home/maya/.local/openclicky/bin/openclicky-linux") == "/home/maya/.local/openclicky/bin/openclicky-linux"
    assert autostart._quoted("/home/maya/My Apps/openclicky") == '"/home/maya/My Apps/openclicky"'
    assert autostart._quoted('/odd/"$name"') == '"/odd/\\"\\$name\\""'
