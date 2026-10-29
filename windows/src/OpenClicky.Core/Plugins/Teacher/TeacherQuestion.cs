//
//  TeacherQuestion.cs
//  OpenClicky (Windows port of Plugins/Teacher/TeacherQuestion.swift)
//
//  Whether a question asks to be taught an idea, so the Teacher can start drawing without waiting
//  for the voice model to decide to. Asked by text "how does a rotary engine work compared to a
//  normal engine", the Realtime model said "let's draw it: 1. the normal engine: a cylinder…" and
//  never called the tool. A question about the screen ("how does this work"), or how to do
//  something ("how do I export"), is not a lesson: pointing answers those. Pure: unit-tested.
//

namespace OpenClicky.Core.Plugins.Teacher;

public static class TeacherQuestion
{
    public static bool IsTeachingQuestion(string question)
    {
        var text = Padded(question.ToLowerInvariant().Replace('’', '\''));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return false;

        // About what is on the screen, or about doing something: not a lesson.
        var pointsAtTheScreen = ContainsAny(text, " this ", " that ", " these ", " those ", " here ", " button", " screen", " menu", " click", " app ");
        // "solve this", "work out the equation on my screen": a worked solution on the board, read
        // off the screenshot. Checked before the screen and how-to rules, which "this" and "help me" trip.
        if (IsProblemToSolve(question)) return true;
        var asksHowToDoSomething = ContainsAny(text, " how do i ", " how can i ", " how should i ", " how to ", " where is ", " where do i ", " can you open ", " help me ");
        if (asksHowToDoSomething) return false;

        if (ContainsAny(text, " teach me ", " teach us ", " explain to me how ", " explain how ", " explain why ", " explain what ")) return true;
        if (pointsAtTheScreen) return false;
        // "how does a rotary engine work", "how do vaccines work", "how is rain formed"
        if (ContainsAny(text, " how does ", " how do ", " how is ", " how are ")
            && ContainsAny(text, " work ", " works ", " formed ", " made ", " happen ", " happens "))
        {
            return true;
        }
        if (ContainsAny(text, " vs ", " versus ", " compared to ", " compared with ", " difference between ", " differ from ")) return true;
        return text.Contains(" explain ", StringComparison.Ordinal)
            || (text.StartsWith(" what is the ", StringComparison.Ordinal) && text.Contains(" theorem ", StringComparison.Ordinal));
    }

    /// <summary>Asks for a worked solution: "solve", "work out", "show me the steps", "step by step".</summary>
    public static bool IsProblemToSolve(string question) =>
        ContainsAny(Padded(question.ToLowerInvariant()), " solve ", " solving ", " work out ", " work this out ", " show me the steps ", " step by step ",
                    " show the working ", " show your working ");

    /// <summary>The question is about something on the screen, so the writer needs the screenshot.</summary>
    public static bool NeedsTheScreen(string question) =>
        IsProblemToSolve(question)
        || ContainsAny(Padded(question.ToLowerInvariant()), " this ", " that ", " these ", " those ", " here ", " screen ", " on my screen ", " above ", " below ");

    /// <summary>Every character that is not a letter, a digit, an apostrophe or a space becomes a space, with a space at
    /// each end, so " word " finds whole words.</summary>
    private static string Padded(string text)
    {
        var characters = text.Select(character => char.IsLetterOrDigit(character) || character is '\'' or ' ' ? character : ' ').ToArray();
        return " " + new string(characters) + " ";
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
}
