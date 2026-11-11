// Set once before any test runs: what the tests must not take from the machine's own shell.json.

using System.Runtime.CompilerServices;
using OpenClicky.Core.Configuration;

namespace OpenClicky.Core.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void PinTheMachineSettings()
    {
        // A user who renamed their assistant would otherwise see the prompt tests fail.
        AssistantName.Pinned = AssistantName.Default;
        // Likewise a machine whose person asked for simpler words or shorter answers.
        OpenClicky.Core.Answers.AnswerStyle.Pinned = [];
    }
}
