import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
// The comparison folder (this script sits in its scripts/ folder). Reads the OpenAI-compatible URL and key from
// %USERPROFILE%\.openclicky\shell.json (customOpenAIUrl, customOpenAIKey); nothing secret is written out.
const dir = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Z]:)/, '$1')), '..');
const s = JSON.parse(fs.readFileSync(process.env.USERPROFILE + '/.openclicky/shell.json', 'utf8'));
const base = s.customOpenAIUrl.replace(/\/$/, '');
const key = s.customOpenAIKey;
const session = crypto.randomUUID();
const system = fs.readFileSync(path.join(dir, 'system.txt'), 'utf8');
const maxTokens = Number(fs.readFileSync(path.join(dir, 'maxtokens.txt'), 'utf8'));
const image = 'data:image/jpeg;base64,' + fs.readFileSync(path.join(dir, 'screen.jpg')).toString('base64');
const prompts = ['school', 'skull', 'plane'];
const models = (await (await fetch(base + '/v1/models', { headers: { Authorization: 'Bearer ' + key } })).json()).data.map(m => m.id);
const outDir = path.join(dir, 'programs'); fs.mkdirSync(outDir, { recursive: true });
const resultsFile = path.join(dir, 'results.json');
const results = fs.existsSync(resultsFile) ? JSON.parse(fs.readFileSync(resultsFile, 'utf8')) : {};
const programIn = text => { const m = text.match(/```(?:js|javascript)?\s*\n([\s\S]*?)(?:```|$)/); return m ? m[1].trim() : ''; };
async function ask(model, prompt, tokens) {
  const body = { model, max_tokens: tokens, messages: [ { role: 'system', content: system }, { role: 'user', content: [
    { type: 'image_url', image_url: { url: image } }, { type: 'text', text: 'Screen 1 (1280×720 pixels)' },
    { type: 'text', text: fs.readFileSync(path.join(dir, `user-${prompt}.txt`), 'utf8') } ] } ] };
  const started = Date.now();
  const ctrl = new AbortController(); const timer = setTimeout(() => ctrl.abort(), 300000);
  try {
    const r = await fetch(base + '/v1/chat/completions', { method: 'POST', signal: ctrl.signal,
      headers: { Authorization: 'Bearer ' + key, 'Content-Type': 'application/json', 'x-opencode-session': session }, body: JSON.stringify(body) });
    const text = await r.text(); const seconds = (Date.now() - started) / 1000;
    if (!r.ok) { let msg = text.slice(0, 300); try { const j = JSON.parse(text); msg = (j.error && (j.error.message || j.error)) || msg; } catch {} return { ok: false, status: r.status, error: String(msg).slice(0, 300), seconds }; }
    const j = JSON.parse(text); const c = j.choices && j.choices[0]; const content = (c && c.message && c.message.content) || '';
    return { ok: true, status: 200, seconds, finish: c && c.finish_reason, content, usage: j.usage };
  } catch (e) { return { ok: false, status: 0, error: e.name === 'AbortError' ? 'timed out after 300 s' : e.message, seconds: (Date.now() - started) / 1000 }; }
  finally { clearTimeout(timer); }
}
const jobs = []; for (const m of models) for (const p of prompts) if (!results[`${m}|${p}`]) jobs.push([m, p]);
console.log(`${models.length} models, ${jobs.length} drawings to ask`);
let next = 0;
async function worker() {
  while (next < jobs.length) {
    const [model, prompt] = jobs[next++];
    let r = await ask(model, prompt, maxTokens);
    if (r.ok && r.finish === 'length' && !r.content.trim()) { r = await ask(model, prompt, maxTokens * 4); r.retried = true; }
    const program = r.ok ? programIn(r.content) : '';
    const safe = model.replace(/[^a-z0-9.-]/gi, '_');
    if (program) fs.writeFileSync(path.join(outDir, `${safe}__${prompt}.js`), program);
    results[`${model}|${prompt}`] = { model, prompt, ok: r.ok, status: r.status, error: r.error, seconds: r.seconds, finish: r.finish,
      chars: r.content ? r.content.length : 0, program: program ? `${safe}__${prompt}.js` : null, none: r.ok && /^\s*NONE\b/.test(r.content || ''),
      outTokens: r.usage && r.usage.completion_tokens, retried: !!r.retried };
    fs.writeFileSync(resultsFile, JSON.stringify(results, null, 1));
    console.log(`${model} ${prompt}: ${r.ok ? (program ? 'program ' + program.length + ' chars' : 'no program (' + (r.finish || '') + ')') : 'failed ' + r.status + ' ' + r.error} in ${r.seconds.toFixed(1)} s`);
  }
}
await Promise.all(Array.from({ length: 5 }, worker));
console.log('done');
