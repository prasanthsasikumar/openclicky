"""Where point_at lands: snapped to the element's text, else located by Claude, else the guess."""

from openclicky_linux import grounding
from openclicky_linux.config import LinuxSettings
from openclicky_linux.grounding import TextLine, grounding_prompt, locate_text, parse_located, resolve

LINES = [TextLine("File Edit View", 0, 0, 140, 20), TextLine("Export as PDF", 300, 200, 130, 20),
         TextLine("Edit", 300, 300, 40, 20), TextLine("Edit", 300, 340, 40, 20)]


def test_the_guess_snaps_to_the_elements_text_unless_it_is_ambiguous_or_generic():
    assert locate_text("Export", (330, 240), LINES, 1280) == (330.0, 210.0)
    assert locate_text("export button", (330, 240), LINES, 1280) == (330.0, 210.0)  # its distinctive word
    assert locate_text("Edit", (320, 320), LINES, 1280) is None  # two rows say Edit, 40 px apart
    assert locate_text("button", (0, 0), LINES, 1280) is None
    assert locate_text("Export", (1200, 700), LINES, 1280) is None  # too far from the guess


def test_claude_is_asked_where_and_its_answer_read():
    prompt = grounding_prompt("export button", "Export", "how do i save as pdf", 1280, 800)
    assert 'it shows the text "Export"' in prompt and 'The user asked: "how do i save as pdf"' in prompt
    assert parse_located("[POINT:120,44:arXiv]", 1280, 800) == ((120.0, 44.0), "arXiv")
    assert parse_located("[POINT:none]", 1280, 800) is None
    assert parse_located("[POINT:2000,44]", 1280, 800) is None


def test_resolve_prefers_text_then_claude_then_the_guess(monkeypatch):
    monkeypatch.setattr(grounding, "ground", lambda *arguments: None)
    assert resolve((330, 240), "export", "Export", None, b"", 1280, 800, LinuxSettings(), lines=LINES) == ((330.0, 210.0), "snapped")
    assert resolve((900, 600), "gear", "", None, b"", 1280, 800, LinuxSettings(), lines=LINES) == ((900, 600), "guess")
    monkeypatch.setattr(grounding, "ground", lambda *arguments: ((310.0, 205.0), "Export"))
    assert resolve((900, 600), "export", "", None, b"", 1280, 800, LinuxSettings(), lines=LINES) == ((330.0, 210.0), "located by claude, snapped")
    # A plan never grounds (too slow mid-sentence).
    monkeypatch.undo()
    assert grounding.ground("x", "", None, b"", 10, 10, LinuxSettings(use_claude_code_plan=True)) is None
