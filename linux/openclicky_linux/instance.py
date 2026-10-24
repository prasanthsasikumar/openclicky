"""One OpenClicky at a time, as on the Mac and Windows: two would each paste every take. The running
one listens on an abstract Unix socket (private to this machine, gone when it exits, nothing on
disk); a second start sends it a word ("settings") and ends, and the running one opens its Settings.
"""

from __future__ import annotations

import os
import socket
import threading
from typing import Callable

_listening: socket.socket | None = None


def socket_address() -> str:
    # Abstract namespace (the leading NUL): per user, per display, so two people logged in at once
    # each have their own.
    display = os.environ.get("WAYLAND_DISPLAY") or os.environ.get("DISPLAY") or "none"
    return f"\0openclicky-linux-{os.getuid()}-{display}"


def forward_to_running(request: str, address: str | None = None) -> bool:
    """True when another OpenClicky took `request`."""
    try:
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as connection:
            connection.settimeout(1.0)
            connection.connect(address or socket_address())
            connection.sendall(request.encode() + b"\n")
            return connection.recv(16).startswith(b"ok")
    except OSError:
        return False


def listen(on_request: Callable[[str], None], address: str | None = None) -> bool:
    """Starts answering later starts (on its own thread). False when the address is taken."""
    global _listening
    server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
    try:
        server.bind(address or socket_address())
    except OSError:
        server.close()
        return False
    server.listen(4)
    _listening = server

    def serve() -> None:
        while True:
            try:
                connection, _ = server.accept()
            except OSError:
                return
            with connection:
                connection.settimeout(1.0)
                try:
                    request = connection.recv(256).decode(errors="replace").strip()
                    connection.sendall(b"ok\n")
                except OSError:
                    continue
            on_request(request)

    threading.Thread(target=serve, name="openclicky-instance", daemon=True).start()
    return True


def stop_listening() -> None:
    global _listening
    if _listening is not None:
        try:
            _listening.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        _listening.close()
        _listening = None
