"""The WebSocket client against a small server on this computer: the handshake, both directions, ping, close."""

import base64
import hashlib
import socket
import struct
import threading

import pytest

from openclicky_linux.websocket import WebSocket, WebSocketError, frame, masked


def server_frame(opcode, payload):
    """A server's frame: unmasked."""
    length = len(payload)
    header = bytes([0x80 | opcode]) + (bytes([length]) if length < 126 else bytes([126]) + struct.pack("!H", length))
    return header + payload


def read_client_frame(connection):
    first, second = connection.recv(2)
    length = second & 0x7F
    if length == 126:
        length = struct.unpack("!H", connection.recv(2))[0]
    mask = connection.recv(4)
    payload = b""
    while len(payload) < length:
        payload += connection.recv(length - len(payload))
    return first & 0x0F, masked(payload, mask)


def serve(listener, script):
    connection, _address = listener.accept()
    request = b""
    while b"\r\n\r\n" not in request:
        request += connection.recv(4096)
    script(connection, request.decode())
    connection.close()


def start(script):
    listener = socket.socket()
    listener.bind(("127.0.0.1", 0))
    listener.listen(1)
    threading.Thread(target=serve, args=(listener, script), daemon=True).start()
    return f"ws://127.0.0.1:{listener.getsockname()[1]}/v3/ws?token=t"


def accept(connection, request):
    key = next(line.split(": ")[1] for line in request.split("\r\n") if line.startswith("Sec-WebSocket-Key"))
    accepted = base64.b64encode(hashlib.sha1((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").encode()).digest()).decode()
    connection.sendall(f"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accepted}\r\n\r\n".encode())


def test_messages_go_both_ways_pings_are_answered_and_close_ends_it():
    seen = {}

    def script(connection, request):
        seen["request"] = request
        accept(connection, request)
        seen["text"] = read_client_frame(connection)
        seen["binary"] = read_client_frame(connection)
        connection.sendall(server_frame(0x9, b"hi") + server_frame(0x1, '{"type":"Begin"}'.encode()) + server_frame(0x2, b"\x00" * 300))
        seen["pong"] = read_client_frame(connection)
        connection.sendall(server_frame(0x8, struct.pack("!H", 1000)))

    url = start(script)
    socket_ = WebSocket(url, headers={"Authorization": "x"})
    socket_.send_text('{"type":"Terminate"}')
    socket_.send_binary(b"\x01\x02" * 100)
    first = socket_.receive()
    assert first.is_text and first.text == '{"type":"Begin"}'
    assert socket_.receive().data == b"\x00" * 300
    assert socket_.receive() is None and socket_.closed
    assert "GET /v3/ws?token=t HTTP/1.1" in seen["request"] and "Authorization: x" in seen["request"]
    assert seen["text"] == (0x1, b'{"type":"Terminate"}') and seen["binary"] == (0x2, b"\x01\x02" * 100)
    assert seen["pong"] == (0xA, b"hi")


def test_a_refused_handshake_says_why():
    def script(connection, request):
        connection.sendall(b"HTTP/1.1 401 Unauthorized\r\nContent-Length: 12\r\n\r\nbad token!!!")

    with pytest.raises(WebSocketError) as refusal:
        WebSocket(start(script))
    assert refusal.value.status == 401 and "bad token" in str(refusal.value)


def test_frames_are_masked_and_sized():
    assert frame(0x1, b"abc", mask=b"\x00\x00\x00\x00") == b"\x81\x83\x00\x00\x00\x00abc"
    assert frame(0x2, b"x" * 200, mask=b"\x00" * 4)[1:4] == bytes([0x80 | 126]) + struct.pack("!H", 200)
    assert masked(masked(b"hello world", b"\x01\x02\x03\x04"), b"\x01\x02\x03\x04") == b"hello world"
