"""The language picker, the region and the script, as the Mac offers them."""

from openclicky_linux import formatter, languages
from openclicky_linux.space import SEEDED_STYLES, DictationSpace


def test_the_region_follows_the_time_zone_until_picked():
    assert languages.detected_region("Asia/Kolkata") == languages.INDIA
    assert languages.detected_region("America/New_York") == languages.WORLD


def test_each_region_offers_its_languages_and_keeps_the_one_in_use():
    india = [code for code, _label in languages.language_choices(languages.INDIA, None)]
    world = [code for code, _label in languages.language_choices(languages.WORLD, None)]
    assert india[0] == world[0] == languages.AUTOMATIC
    assert "hi-IN" in india and "hi-IN" not in world and "fr-FR" in world and "fr-FR" not in india
    assert "hi-IN" in [code for code, _label in languages.language_choices(languages.WORLD, "hi-IN")]
    assert ("xx-YY", "xx-YY") in languages.language_choices(languages.WORLD, "xx-YY")


def test_the_script_row_and_the_clean_up_prompt():
    assert languages.offers_script(languages.INDIA, None, languages.NATIVE)
    assert languages.offers_script(languages.WORLD, "hi-IN", languages.NATIVE)
    assert not languages.offers_script(languages.WORLD, "fr-FR", languages.NATIVE)
    prompt = formatter.system_prompt(SEEDED_STYLES[-1], DictationSpace(), language_code="hi-IN", script=languages.ROMAN)
    assert "The language is Hindi (hi). Write it in roman letters (latin script), the way people type it on a phone." in prompt
    assert "If the speech is in an Indian language, write it in roman letters" in formatter.system_prompt(
        SEEDED_STYLES[-1], DictationSpace(), script=languages.ROMAN)
