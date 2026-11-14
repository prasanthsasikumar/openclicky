// Builds the comparison page from results.json and the screenshots in shots/.
import fs from 'node:fs';
import path from 'node:path';
// The comparison folder (this script sits in its scripts/ folder); the page is written there unless a folder is given.
const dir = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1')), '..');
const outDir = process.argv[2] || dir;
const results = JSON.parse(fs.readFileSync(path.join(dir, 'results.json'), 'utf8'));
const renders = fs.existsSync(path.join(dir, 'renders.json')) ? JSON.parse(fs.readFileSync(path.join(dir, 'renders.json'), 'utf8')) : {};
fs.mkdirSync(path.join(outDir, 'shots'), { recursive: true });
const prompts = [['school', 'School'], ['skull', 'Skull'], ['plane', 'Plane']];
const models = [...new Set(Object.values(results).map(r => r.model))];
const score = m => prompts.reduce((n, [p]) => n + (renders[`${m}|${p}`]?.shot && !renders[`${m}|${p}`]?.errors?.length ? 2 : renders[`${m}|${p}`]?.shot ? 1 : 0), 0);
models.sort((a, b) => score(b) - score(a) || a.localeCompare(b));
const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
let rows = '';
for (const m of models) {
  let cells = '';
  for (const [p] of prompts) {
    const r = results[`${m}|${p}`] || {}; const v = renders[`${m}|${p}`] || {};
    let body;
    if (v.shot) {
      if (path.resolve(outDir) !== dir) fs.copyFileSync(path.join(dir, 'shots', v.shot), path.join(outDir, 'shots', v.shot));
      body = `<a href="shots/${esc(v.shot)}" target="_blank"><img loading="lazy" src="shots/${esc(v.shot)}" alt="${esc(m)} ${p}"></a>`;
    } else if (!r.ok) body = `<div class="fail">Failed${r.status ? ' (' + r.status + ')' : ''}<br><small>${esc(r.error)}</small></div>`;
    else if (r.none) body = `<div class="fail">Answered NONE<br><small>refused to draw</small></div>`;
    else if (!r.program) body = `<div class="fail">No program<br><small>${esc(r.finish || '')}, ${r.chars} chars</small></div>`;
    else body = `<div class="fail">Didn't render</div>`;
    const meta = [r.seconds != null ? r.seconds.toFixed(0) + ' s' : '', r.outTokens ? r.outTokens + ' tok' : '', v.errors?.length ? `<span class="err" title="${esc(v.errors.join('\n'))}">${v.errors.length} error${v.errors.length > 1 ? 's' : ''}</span>` : (v.shot ? '<span class="good">no errors</span>' : '')].filter(Boolean).join(' · ');
    cells += `<td>${body}<div class="meta">${meta}</div></td>`;
  }
  rows += `<tr><th scope="row">${esc(m)}</th>${cells}</tr>`;
}
const ok = models.filter(m => score(m) > 0).length;
fs.writeFileSync(path.join(outDir, 'index.html'), `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>Drawing model comparison</title><style>
:root{--bg:#f6f6f4;--fg:#1d1d1f;--muted:#6b6b70;--card:#fff;--line:#e3e3e0;--bad:#b42318;--ok:#067647}
@media (prefers-color-scheme:dark){:root{--bg:#161618;--fg:#ececee;--muted:#9a9aa1;--card:#202024;--line:#2f2f35;--bad:#f97066;--ok:#47cd89}}
body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.45 system-ui,Segoe UI,sans-serif}
header{padding:24px 16px 8px;max-width:1500px;margin:auto}h1{margin:0 0 4px;font-size:22px}p{margin:4px 0;color:var(--muted)}
.wrap{overflow-x:auto;padding:8px 16px 40px;max-width:1500px;margin:auto}table{border-collapse:separate;border-spacing:8px;min-width:900px;width:100%}
th{font-weight:600;text-align:left}thead th{position:sticky;top:0;background:var(--bg);padding:6px 4px;z-index:1}
tbody th{width:170px;vertical-align:top;padding-top:10px;word-break:break-word}td{background:var(--card);border:1px solid var(--line);border-radius:10px;padding:6px;vertical-align:top;width:30%}
img{display:block;width:100%;border-radius:6px;aspect-ratio:16/9;object-fit:cover;background:#262626}
.fail{aspect-ratio:16/9;display:flex;flex-direction:column;align-items:center;justify-content:center;text-align:center;color:var(--bad);border-radius:6px;background:color-mix(in srgb,var(--bad) 8%,transparent);padding:8px}
.fail small{color:var(--muted);display:block;max-width:95%;overflow-wrap:anywhere}.meta{color:var(--muted);font-size:12px;margin-top:5px}.err{color:var(--bad)}.good{color:var(--ok)}
</style></head><body><header><h1>Drawing model comparison</h1>
<p>Every model on your OpenCode Go key, given OpenClicky's real drawing instructions and the same three questions: "make me a 3D model of a school", "show me a 3D model of a human skull", "make me a 3D model of an airplane". Each picture is the finished drawing as the stage renders it. Time is how long the whole answer took to arrive.</p>
<p>${models.length} models · ${ok} drew at least one · sorted best first. Click a picture to open it full size.</p></header>
<div class="wrap"><table><thead><tr><th>Model</th>${prompts.map(([, t]) => `<th>${t}</th>`).join('')}</tr></thead><tbody>${rows}</tbody></table></div></body></html>`);
console.log('page written', path.join(outDir, 'index.html'));
