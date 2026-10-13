from openclicky_linux.formatter import apply_dictionary, format_locally, remove_fillers, sentence_cased
from openclicky_linux.space import DictationSpace, DictionaryTerm, SpokenShortcut


def test_fillers_go_with_the_comma_after_them():
    assert remove_fillers("um, so I think uh we should go") == "so I think we should go"


def test_filler_letters_inside_words_stay():
    assert remove_fillers("the umbrella is huge") == "the umbrella is huge"


def test_dictionary_replaces_whole_words_longest_alias_first():
    space = DictationSpace(dictionary=(
        DictionaryTerm(written="Aaditya", heard_as=("aditya",)),
        DictionaryTerm(written="Aaditya Kshatriya", heard_as=("aditya shatriya",)),
    ))
    assert apply_dictionary("call aditya shatriya and aditya", space) == "call Aaditya Kshatriya and Aaditya"
    assert apply_dictionary("adityas", space) == "adityas"


def test_sentence_case_capitalises_after_each_end_but_adds_nothing():
    assert sentence_cased("hello there. how are you? fine") == "Hello there. How are you? Fine"
    assert sentence_cased("git status") == "Git status"
    assert sentence_cased("3 apples. ok") == "3 apples. Ok"


def test_a_whole_take_shortcut_wins():
    space = DictationSpace(shortcuts=(SpokenShortcut(trigger="my sign-off", replacement="Cheers,\nMaya"),))
    assert format_locally("My sign off.", space) == "Cheers,\nMaya"
    assert format_locally("my sign off please", space) == "My sign off please"


def test_empty_and_whitespace_takes_are_empty():
    assert format_locally("   \t ", DictationSpace()) == ""
