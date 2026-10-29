// Port of macos/OpenClicky/OpenClicky/EmailActions.swift.
//
// "Check my email" and "draft an email to Ana saying…". Windows has no Apple Mail to script, so both
// inboxes are web pages opened in the browser: Gmail (the Mac's fallback) or Outlook. A check opens
// the inbox and the model reads it from a fresh screenshot; a draft opens a pre-filled compose
// window. Nothing here sends: there is no send, and never will be by voice.

namespace OpenClicky.Core.Actions;

public enum EmailApp
{
    Gmail,
    Outlook,
}

public static class EmailApps
{
    /// <summary>The value in shell.json and the tool schema: "gmail", "outlook".</summary>
    public static string RawValue(this EmailApp app) => app == EmailApp.Outlook ? "outlook" : "gmail";

    public static string Title(this EmailApp app) => app == EmailApp.Outlook ? "Outlook" : "Gmail";

    public static EmailApp? Named(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "gmail" => EmailApp.Gmail,
        "outlook" => EmailApp.Outlook,
        _ => null,
    };

    /// <summary>
    /// Which inbox a check or draft goes to: the one the person named, else the setting (shell.json's
    /// emailApp), else Gmail, as on a Mac without Mail set up. "mail" (the Mac's Apple Mail, from a
    /// shared shell.json or a model trained on the Mac schema) reads as no choice.
    /// </summary>
    public static EmailApp Choose(string? requested, string? setting) =>
        Named(requested) ?? Named(setting) ?? EmailApp.Gmail;

    public static Uri InboxUrl(this EmailApp app) => app == EmailApp.Outlook
        ? new Uri("https://outlook.live.com/mail/0/inbox")
        : new Uri("https://mail.google.com/mail/u/0/#inbox");

    /// <summary>
    /// The compose window, pre-filled. Every field is percent-encoded (UTF-8): a bare `+` is a space,
    /// `&amp;` and `#` would cut the query, a newline must travel as %0A.
    /// </summary>
    public static Uri ComposeUrl(this EmailApp app, string recipient, string subject, string body)
    {
        var to = Uri.EscapeDataString(recipient);
        var su = Uri.EscapeDataString(subject);
        var text = Uri.EscapeDataString(body);
        return app == EmailApp.Outlook
            ? new Uri($"https://outlook.live.com/mail/0/deeplink/compose?to={to}&subject={su}&body={text}")
            : new Uri($"https://mail.google.com/mail/?view=cm&fs=1&to={to}&su={su}&body={text}");
    }
}
