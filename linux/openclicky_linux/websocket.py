"""A small WebSocket client (RFC 6455) on the standard library, for the two sockets OpenClicky keeps
open: AssemblyAI's streaming dictation (`streaming.py`) and OpenAI Realtime (`realtime.py`). Client
frames are masked, as the protocol requires; a ping is answered with a pong; text, binary and close
are handed up. One reader thread at a time; sends may come from any thread (they are serialised).
"""

from __future__ import annotations

import base64
import os
import socket
import ssl
import struct
import threading
import urllib.parse
from dataclasses import dataclass

OPCODE_CONTINUATION, OPCODE_TEXT, OPCODE_BINARY, OPCODE_CLOSE, OPCODE_PING, OPCODE_PONG = 0x0, 0x1, 0x2, 0x8, 0x9, 0xA
MAXIMUM_MESSAGE_BYTES = 16 * 1024 * 1024


class WebSocketError(Exception):
    """The handshake was refused (`status`, with the body's start) or the connection broke."""

    def __init__(self, message: str, status: int | None = None) -> None:
        super().__init__(message)
        self.status = status


@dataclass(frozen=True)
class Message:
    is_text: bool
    data: bytes

    @property
    def text(self) -> str:
        return self.data.decode("utf-8", "replace")


def masked(payload: bytes, mask: bytes) -> bytes:
    """The payload XORed with the four-byte mask, a whole message at once (audio is sent many times a second)."""
    if not payload:
        return payload
    repeated = (mask * (len(payload) // 4 + 1))[:len(payload)]
    return (int.from_bytes(payload, "big") ^ int.from_bytes(repeated, "big")).to_bytes(len(payload), "big")


def frame(opcode: int, payload: bytes, mask: bytes | None = None) -> bytes:
    """One final, masked client frame."""
    mask = mask if mask is not None else os.urandom(4)
    header = bytes([0x80 | opcode])
    length = len(payload)
    if length < 126:
        header += bytes([0x80 | length])
    elif length < 1 << 16:
        header += bytes([0x80 | 126]) + struct.pack("!H", length)
    else:
        header += bytes([0x80 | 127]) + struct.pack("!Q", length)
    return header + mask + masked(payload, mask)


class WebSocket:
    def __init__(self, url: str, headers: dict[str, str] | None = None, timeout_seconds: float = 15.0) -> None:
        parsed = urllib.parse.urlsplit(url)
        if parsed.scheme not in ("ws", "wss"):
            raise ValueError(f"not a websocket url: {parsed.scheme}")
        port = parsed.port or (443 if parsed.scheme == "wss" else 80)
        raw = socket.create_connection((parsed.hostname, port), timeout=timeout_seconds)
        if parsed.scheme == "wss":
            raw = ssl.create_default_context().wrap_socket(raw, server_hostname=parsed.hostname)
        self._socket = raw
        self._send_lock = threading.Lock()
        self._buffer = b""
        self.closed = False
        key = base64.b64encode(os.urandom(16)).decode("ascii")
        path = (parsed.path or "/") + (f"?{parsed.query}" if parsed.query else "")
        lines = [f"GET {path} HTTP/1.1", f"Host: {parsed.hostname}" + (f":{parsed.port}" if parsed.port else ""), "Upgrade: websocket",
                 "Connection: Upgrade", f"Sec-WebSocket-Key: {key}", "Sec-WebSocket-Version: 13"]
        lines += [f"{name}: {value}" for name, value in (headers or {}).items()]
        self._socket.sendall(("\r\n".join(lines) + "\r\n\r\n").encode("utf-8"))
        head = self._read_until(b"\r\n\r\n")
        status_line, *header_lines = head.decode("iso-8859-1").split("\r\n")
        parts = status_line.split(" ", 2)
        status = int(parts[1]) if len(parts) > 1 and parts[1].isdigit() else 0
        if status != 101:
            response_headers = {line.split(":", 1)[0].strip().lower(): line.split(":", 1)[1].strip() for line in header_lines if ":" in line}
            body = self._read_some_body(int(response_headers.get("content-length", "0") or 0))
            self._socket.close()
            raise WebSocketError(f"the server refused the socket ({status}): {body[:200]}", status=status)
        # A read waits as long as it takes from here on; `close` ends it.
        self._socket.settimeout(None)

    def _read_until(self, marker: bytes) -> bytes:
        while marker not in self._buffer:
            chunk = self._socket.recv(4096)
            if not chunk:
                raise WebSocketError("the connection closed during the handshake")
            self._buffer += chunk
            if len(self._buffer) > 65536:
                raise WebSocketError("the handshake answer was too long")
        head, self._buffer = self._buffer.split(marker, 1)
        return head

    def _read_some_body(self, length: int) -> str:
        try:
            while len(self._buffer) < min(length, 2048):
                chunk = self._socket.recv(4096)
                if not chunk:
                    break
                self._buffer += chunk
        except OSError:
            pass
        return self._buffer.decode("utf-8", "replace")

    def _read_exactly(self, count: int) -> bytes:
        while len(self._buffer) < count:
            chunk = self._socket.recv(max(4096, count - len(self._buffer)))
            if not chunk:
                raise WebSocketError("the connection closed")
            self._buffer += chunk
        data, self._buffer = self._buffer[:count], self._buffer[count:]
        return data

    def _send(self, opcode: int, payload: bytes) -> None:
        with self._send_lock:
            if self.closed:
                raise WebSocketError("the socket is closed")
            self._socket.sendall(frame(opcode, payload))

    def send_text(self, text: str) -> None:
        self._send(OPCODE_TEXT, text.encode("utf-8"))

    def send_binary(self, data: bytes) -> None:
        self._send(OPCODE_BINARY, data)

    def receive(self) -> Message | None:
        """The next text or binary message; None once the server closed the socket."""
        pieces: list[bytes] = []
        message_opcode = None
        while True:
            first, second = self._read_exactly(2)
            final, opcode = bool(first & 0x80), first & 0x0F
            length = second & 0x7F
            if length == 126:
                length = struct.unpack("!H", self._read_exactly(2))[0]
            elif length == 127:
                length = struct.unpack("!Q", self._read_exactly(8))[0]
            if length > MAXIMUM_MESSAGE_BYTES:
                raise WebSocketError("a message was too large")
            mask = self._read_exactly(4) if second & 0x80 else None
            payload = self._read_exactly(length)
            if mask:
                payload = masked(payload, mask)
            if opcode == OPCODE_PING:
                self._send(OPCODE_PONG, payload)
                continue
            if opcode == OPCODE_PONG:
                continue
            if opcode == OPCODE_CLOSE:
                self.close()
                return None
            if opcode in (OPCODE_TEXT, OPCODE_BINARY):
                message_opcode = opcode
            pieces.append(payload)
            if final:
                return Message(is_text=message_opcode == OPCODE_TEXT, data=b"".join(pieces))

    def close(self) -> None:
        with self._send_lock:
            if self.closed:
                return
            self.closed = True
            try:
                self._socket.sendall(frame(OPCODE_CLOSE, struct.pack("!H", 1000)))
            except OSError:
                pass
        try:
            self._socket.shutdown(socket.SHUT_RDWR)
        except OSError:
            pass
        self._socket.close()
