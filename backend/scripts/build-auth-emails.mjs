// Builds the Supabase Auth email templates from one layout, so the sign-in code, sign-up code
// and email-confirm mails stay alike. Writes the PATCH body for /config/auth (see
// docs/accounts-runbook.md §4) and a preview of each mail with a sample code.
//
//   node backend/scripts/build-auth-emails.mjs [outDir]   (default backend/supabase/emails)
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const outDir = process.argv[2] ?? new URL("../supabase/emails", import.meta.url).pathname;

const ink = "#22201C";
const muted = "#5E5A52";
const paper = "#F4F1EA";
const line = "#ECE7DC";
const pointer = "#F0452B";
const font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Helvetica,Arial,sans-serif";
const mono = "ui-monospace,SFMono-Regular,Menlo,Consolas,monospace";

const key = (k) =>
  `<span style="display:inline-block;padding:1px 6px;border:1px solid ${line};border-bottom-width:2px;border-radius:5px;background:#fff;font-size:13px;color:${ink};white-space:nowrap">${k}</span>`;

const tips = [
  ["Ask, and it points", `Hold ${key("⌃ control")} + ${key("⌥ option")} and ask out loud: <i>“where do I check battery health?”</i> A pointer flies to the answer on your screen.`],
  ["Talk instead of typing", `Hold ${key("fn")} in any text box, speak, let go. Your words are typed where your cursor is, tidied for the app you're in.`],
  ["Rewrite what you wrote", `Select some text, hold ${key("fn")} + ${key("⌃")} and say <i>“make it formal”</i>.`],
];

const tipsBlock = `
<tr><td style="padding:28px 32px 4px;border-top:1px solid ${line}">
  <p style="margin:0 0 14px;font-size:13px;letter-spacing:.08em;text-transform:uppercase;color:${muted}">Three things to try</p>
  ${tips
    .map(
      ([title, body], i) => `
  <table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="margin:0 0 16px"><tr>
    <td valign="top" width="30" style="font:600 15px/22px ${font};color:${pointer}">${i + 1}</td>
    <td style="font:15px/22px ${font};color:${ink}"><b>${title}.</b> ${body}</td>
  </tr></table>`,
    )
    .join("")}
  <p style="margin:4px 0 0;font-size:13px;line-height:20px;color:${muted}">On Windows: hold Ctrl + Win to ask, right Ctrl to dictate. On Linux: left Ctrl + Alt to ask, left Ctrl to dictate.</p>
</td></tr>`;

function layout({ preheader, heading, middle }) {
  return `<!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><meta name="color-scheme" content="light only"><title>OpenClicky</title></head>
<body style="margin:0;padding:0;background:${paper}">
<div style="display:none;max-height:0;overflow:hidden;opacity:0">${preheader}</div>
<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="background:${paper}"><tr><td align="center" style="padding:32px 12px">
<table role="presentation" cellpadding="0" cellspacing="0" border="0" width="100%" style="max-width:520px;background:#fff;border-radius:14px;font-family:${font};color:${ink}">
<tr><td style="padding:28px 32px 0;font:700 17px/1 ${font};color:${ink}"><span style="color:${pointer}">●</span>&nbsp;openclicky</td></tr>
<tr><td style="padding:22px 32px 26px">
  <h1 style="margin:0 0 18px;font:600 22px/1.3 ${font};color:${ink}">${heading}</h1>
  ${middle}
</td></tr>
${tipsBlock}
<tr><td style="padding:20px 32px 28px;border-top:1px solid ${line};font:14px/22px ${font};color:${muted}">
  Free and open source. Questions or ideas? Just reply, a person reads every one.<br>
  <a href="https://openclicky.flowsxr.com" style="color:${ink}">Website</a> &nbsp;·&nbsp;
  <a href="https://discord.gg/zGGERnM5eq" style="color:${ink}">Discord</a> &nbsp;·&nbsp;
  <a href="https://github.com/prasanthsasikumar/openclicky" style="color:${ink}">GitHub</a>
</td></tr>
</table>
<p style="margin:16px 0 0;font:12px/18px ${font};color:${muted}">You got this because someone typed this address into OpenClicky.<br>If it wasn't you, ignore it: nothing happens without the code.</p>
</td></tr></table>
</body></html>`;
}

const codeBox = (lead) => `
  <p style="margin:0 0 14px;font:16px/24px ${font}">${lead}</p>
  <div style="margin:0 0 14px;padding:16px 0;background:${paper};border-radius:10px;text-align:center;font:700 34px/1 ${mono};letter-spacing:.3em;color:${ink}">{{ .Token }}</div>
  <p style="margin:0;font:14px/22px ${font};color:${muted}">It works for an hour.</p>`;

const templates = {
  magic_link: {
    subject: "Your OpenClicky code",
    html: layout({
      preheader: "{{ .Token }} is your sign-in code. It works for an hour.",
      heading: "Your sign-in code",
      middle: codeBox("Type this into OpenClicky to sign in:"),
    }),
  },
  confirmation: {
    subject: "Your OpenClicky code",
    html: layout({
      preheader: "{{ .Token }} is your code. Welcome to OpenClicky.",
      heading: "Welcome to OpenClicky",
      middle: codeBox("Type this code into OpenClicky to finish making your free account:"),
    }),
  },
  email_change: {
    subject: "Your OpenClicky code",
    html: layout({
      preheader: "{{ .Token }} is your code. Welcome to OpenClicky.",
      heading: "Welcome to OpenClicky",
      middle: codeBox("Type this code into OpenClicky to confirm this email for your free account:"),
    }),
  },
};

mkdirSync(outDir, { recursive: true });
const settings = {};
for (const [name, { subject, html }] of Object.entries(templates)) {
  settings[`mailer_subjects_${name}`] = subject;
  settings[`mailer_templates_${name}_content`] = html;
  writeFileSync(
    join(outDir, `${name}.preview.html`),
    html.replaceAll("{{ .Token }}", "739688"),
  );
}
writeFileSync(join(outDir, "auth-email-settings.json"), JSON.stringify(settings, null, 2) + "\n");
console.log(`wrote ${Object.keys(templates).length} templates to ${outDir}`);
