const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs'); const { execFileSync } = require('child_process');
const ROOT = path.resolve(__dirname, '..');
const HTML = require('url').pathToFileURL(path.join(ROOT, 'logi-cool-j-builder.html')).href;
// The page loads js-yaml and ai-assist from ../lib, so any http(s) request is a failure.
const OUT = fs.mkdtempSync(path.join(require('os').tmpdir(), 'lcj-'));
// Windows has no python3 (only a Store stub), so default to python there.
const PY = process.env.PYTHON || (process.platform === 'win32' ? 'python' : 'python3');
const results = []; const ok = (name, cond, extra='') => { results.push(`${cond ? 'PASS' : 'FAIL'}  ${name}${extra ? '  — ' + extra : ''}`); };

async function newPage(browser, preload) {
  const ctx = await browser.newContext({ acceptDownloads: true });
  await ctx.addInitScript(() => { delete window.showSaveFilePicker; });
  if (preload) await ctx.addInitScript(preload);
  const page = await ctx.newPage();
  page.errors = []; page.netReqs = [];
  page.on('pageerror', e => page.errors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') page.errors.push(m.text()); });
  page.on('request', r => { const u = r.url(); if (!u.startsWith('file:') && !u.startsWith('data:') && !u.startsWith('blob:')) page.netReqs.push(u); });
  await page.goto(HTML);
  return { ctx, page };
}
const prof = (page) => page.evaluate(() => JSON.parse(JSON.stringify(STATE.activeProfile())));
async function cell(page, i) { await page.click(`#kp-grid [data-kp-index="${i}"]`); }
async function addStep(page, type, value) {
  await page.click('#kp-add-step');
  const rows = page.locator('#kp-steps-list .step-row');
  const n = await rows.count(); const row = rows.nth(n - 1);
  await row.locator('select').selectOption(type);
  const inp = page.locator('#kp-steps-list .step-row').nth(n - 1).locator('input');
  await inp.fill(value);
}

(async () => {
  const browser = await chromium.launch();
  let { ctx, page } = await newPage(browser);
  page.on('dialog', d => d.accept());

  // fresh start
  let p = await prof(page);
  ok('fresh load creates 1 profile/1 page', p.pages.length === 1);
  await page.fill('#kp-prof-name', 'Test Two Page');
  // add page 2
  await page.click('#kp-tab-add-page'); await page.fill('#m-name', 'Second'); await page.click('#confirm-ok');
  p = await prof(page);
  ok('page 2 added with Home on 8', p.pages.length === 2 && p.pages[1].buttons[8].steps[0].type === 'go_to_page');
  // back to page 1
  await page.click('#kp-page-tabs .kp-page-tab >> nth=0');
  await cell(page, 0); await page.fill('#kp-btn-label', 'Cline'); await addStep(page, 'key', 'Ctrl+Alt+L');
  await cell(page, 1); await page.fill('#kp-btn-label', 'Hello'); await addStep(page, 'text', 'yes');
  await cell(page, 2); await page.fill('#kp-btn-label', 'To P2'); await addStep(page, 'go_to_page', '2');
  p = await prof(page);
  ok('shortcut/text/nav buttons stored', p.pages[0].buttons[0].steps[0].value === 'Ctrl+Alt+L' && p.pages[0].buttons[1].steps[0].value === 'yes' && p.pages[0].buttons[2].steps[0].value === '2');
  // rename page 2
  await page.click('#kp-page-tabs .kp-page-tab >> nth=1'); await page.fill('#kp-page-name', 'Two Renamed');
  p = await prof(page);
  ok('rename page keeps nav target', p.pages[1].name === 'Two Renamed' && p.pages[0].buttons[2].steps[0].value === '2');
  await page.click('#kp-page-tabs .kp-page-tab >> nth=0');
  // copy 0 -> 3
  await cell(page, 0); await page.click('#kp-btn-copy'); await cell(page, 3);
  p = await prof(page);
  ok('copy into empty slot', p.pages[0].buttons[3].label === 'Cline' && p.pages[0].buttons[0].label === 'Cline');
  ok('copy is a deep clone', await page.evaluate(() => { const b = STATE.activePage().buttons; return b[0].steps !== b[3].steps; }));
  // move 3 -> 4
  await cell(page, 3); await page.click('#kp-btn-move'); await cell(page, 4);
  p = await prof(page);
  ok('move into empty slot clears source', p.pages[0].buttons[4].label === 'Cline' && !p.pages[0].buttons[3].label);
  // swap 4 <-> 5 (empty)
  await cell(page, 4); await page.click('#kp-btn-swap'); await cell(page, 5);
  p = await prof(page);
  ok('swap with empty slot', p.pages[0].buttons[5].label === 'Cline' && !p.pages[0].buttons[4].label);
  // clear 5 + undo
  await cell(page, 5); await page.click('#kp-btn-clear');
  p = await prof(page); ok('clear', !p.pages[0].buttons[5].label);
  await page.click('#kp-btn-undo');
  p = await prof(page); ok('undo restores cleared button', p.pages[0].buttons[5].label === 'Cline');
  // move onto itself = no-op
  await cell(page, 0); await page.click('#kp-btn-move'); await cell(page, 0);
  p = await prof(page); ok('move onto same slot is a no-op', p.pages[0].buttons[0].label === 'Cline');
  // copy onto occupied, dismiss confirm
  page.removeAllListeners('dialog'); page.on('dialog', d => d.dismiss());
  await cell(page, 0); await page.click('#kp-btn-copy'); await cell(page, 1);
  p = await prof(page); ok('copy onto occupied asks & respects Cancel', p.pages[0].buttons[1].label === 'Hello');
  await page.keyboard.press('Escape');
  ok('Escape cancels armed op', await page.evaluate(() => STATE.clipboardOp === null));
  page.removeAllListeners('dialog'); page.on('dialog', d => d.accept());
  // cross-page copy 0 -> page 2 cell 0
  await cell(page, 0); await page.click('#kp-btn-copy'); await page.click('#kp-page-tabs .kp-page-tab >> nth=1'); await cell(page, 0);
  p = await prof(page); ok('cross-page copy', p.pages[1].buttons[0].label === 'Cline');
  await page.click('#kp-page-tabs .kp-page-tab >> nth=0');
  // drag swap cells 0 <-> 6
  await page.dragAndDrop('#kp-grid [data-kp-index="0"]', '#kp-grid [data-kp-index="6"]');
  p = await prof(page); ok('drag-and-drop swap', p.pages[0].buttons[6].label === 'Cline' && !p.pages[0].buttons[0].label);
  ok('no JS errors so far', page.errors.length === 0, page.errors.join(' | '));
  await page.click('#kp-btn-undo');
  // keyboard select
  await page.focus('#kp-grid [data-kp-index="1"]'); await page.keyboard.press('Enter');
  ok('keyboard Enter selects cell', (await prof(page)).activeButtonIndex === 1);
  // validation: secret + bad modifier
  const SECRET = 'gsk_' + 'A1b2C3d4E5f6G7h8I9j0K1l2M3n4';
  await cell(page, 7); await page.fill('#kp-btn-label', 'Bad'); await addStep(page, 'text', SECRET);
  let vtxt = await page.textContent('#kp-validation-box');
  ok('secret refused', /API key/.test(vtxt) && !vtxt.includes(SECRET), vtxt);
  page.removeAllListeners('dialog'); let alerted = '';
  page.on('dialog', d => { alerted += d.message(); d.accept(); });
  await page.click('#kp-save-yaml');
  ok('save blocked on secret, alert hides value', alerted.includes('API key') && !alerted.includes(SECRET));
  await page.locator('#kp-steps-list .step-row').nth(0).locator('input').fill('Cmd+C');
  await page.locator('#kp-steps-list .step-row').nth(0).locator('select').selectOption('key');
  vtxt = await page.textContent('#kp-validation-box');
  ok('unknown modifier flagged', /unknown modifier 'Cmd'/.test(vtxt), vtxt);
  await page.click('#kp-btn-clear');
  // modal focus bug: type in modal step field
  await page.click('#dial-grid [data-index="1"]');
  await page.locator('#ed-steps .step-row input').first().click();
  await page.keyboard.type('!!');
  const mv = await page.evaluate(() => STATE.activePage().buttons[1].steps[0].value);
  ok('modal step input keeps focus while typing', mv === 'yes!!', mv);
  await page.locator('#ed-steps .step-row input').first().fill('yes');
  await page.keyboard.press('Escape');
  ok('Escape closes editor modal', await page.isHidden('#editor-modal'));
  // reload persistence
  await page.reload();
  p = await prof(page);
  ok('persists across reload', p.name === 'Test Two Page' && p.pages[1].name === 'Two Renamed' && p.pages[0].buttons[0].label === 'Cline');
  // build button: no bridge request, downloads yaml
  const [dl] = await Promise.all([page.waitForEvent('download'), page.click('#kp-build-lp5')]);
  const yPath = path.join(OUT, 'keypad-layout.yaml'); await dl.saveAs(yPath);
  const out = await page.textContent('#kp-stdout-box');
  ok('Build shows CLI command, no network', out.includes('python yaml2lp5.py') && page.netReqs.length === 0, page.netReqs.join(','));
  const y = fs.readFileSync(yPath, 'utf8');
  ok('YAML quotes "yes" and omits profile_id when blank', y.includes('text: "yes"') && !y.includes('profile_id'));
  fs.writeFileSync(path.join(OUT, 'exported.yaml'), y);
  // converter
  const conv = path.join(ROOT, 'lp5-build', 'yaml2lp5.py');
  const o = execFileSync(PY, [conv, yPath, path.join(OUT, 'test.lp5')], { encoding: 'utf8' });
  ok('converter accepts exported YAML', /2 pages/.test(o), o.trim());
  const chk = execFileSync(PY, ['-c', `
import zipfile,json
d=json.loads(zipfile.ZipFile('${OUT.replace(/\\/g, '/')}/test.lp5').read('ProfileInfo.json'))
ws=d['layout']['layoutModes'][0]['workspaces'][0]; pg=ws['pressPages']
txt=[e['actionParameters'].get('text') for m in d['macroCommands'] for e in m['actionEditorCommands']]
nav=pg[0]['controls'][2]['pressAction']
print(len(pg), 'yes' in txt, nav.endswith(pg[1]['name']), pg[1]['displayName'])`], { encoding: 'utf8' }).trim();
  ok('lp5: text stays "yes", page-1 nav points at page 2', chk === '2 True True Two Renamed', chk);
  // delete page remap: add page 3, nav to 3 from page1 btn 8, delete page 2
  await page.click('#kp-tab-add-page'); await page.fill('#m-name', 'Third'); await page.click('#confirm-ok');
  await page.click('#kp-page-tabs .kp-page-tab >> nth=0'); await cell(page, 8); await page.fill('#kp-btn-label', 'To P3'); await addStep(page, 'go_to_page', '3');
  await page.click('#kp-page-tabs .kp-page-tab >> nth=1'); await page.click('#btn-del-page');
  p = await prof(page);
  ok('delete page remaps go_to_page and blanks orphans', p.pages.length === 2 && p.pages[0].buttons[8].steps[0].value === '2' && p.pages[0].buttons[2].steps[0].value === '', JSON.stringify([p.pages[0].buttons[8].steps, p.pages[0].buttons[2].steps]));
  ok('no JS errors in main flow', page.errors.length === 0, page.errors.join(' | '));
  // JSON backup round trip
  const [dl2] = await Promise.all([page.waitForEvent('download'), page.click('#btn-export-json')]);
  await dl2.saveAs(path.join(OUT, 'backup.json'));
  await ctx.close();

  // legacy builder migration
  ({ ctx, page } = await newPage(browser, () => {
    if (!localStorage.getItem('mx-console-v3')) localStorage.setItem('mx-console-builder-state', JSON.stringify({ pages: [{ id: 'page-1', name: 'Old Home', buttons: [{ id: 0, emoji: '🧠', label: 'DS', actionType: 'url', action: 'https://chat.deepseek.com' }, { id: 1, label: 'Cline', actionType: 'keystroke', action: 'Ctrl+Alt+L' }] }], activePageId: 'page-1' }));
  }));
  p = await prof(page);
  ok('migrates old builder storage', p.pages[0].name === 'Old Home' && p.pages[0].buttons[0].steps[0].type === 'url' && p.pages[0].buttons[1].steps[0].value === 'Ctrl+Alt+L');
  ok('old builder key left untouched', await page.evaluate(() => !!localStorage.getItem('mx-console-builder-state')));
  await ctx.close();

  // corrupt v3 → recovery copy
  ({ ctx, page } = await newPage(browser, () => {
    if (!sessionStorage.getItem('seeded')) { localStorage.setItem('mx-console-v3', '{broken json'); sessionStorage.setItem('seeded', '1'); }
  }));
  const keys = await page.evaluate(() => Object.keys(localStorage));
  ok('unreadable save kept as recovery copy', keys.some(k => k.startsWith('mx-console-v3.recovery-')), keys.join(','));
  // old v3 save from original builder (activeButtonIndex null, id mx-main)
  await ctx.close();
  ({ ctx, page } = await newPage(browser, () => {
    if (!sessionStorage.getItem('seeded')) { sessionStorage.setItem('seeded', '1');
      localStorage.setItem('mx-console-v3', JSON.stringify({ version: 3, activeProfileId: 'mx-main', profiles: [{ id: 'mx-main', name: 'Orig', pages: [{ id: 'pg1', name: 'Home', buttons: Array.from({length: 9}, (_, i) => ({ id: i, emoji: '', label: i ? '' : 'X', imageData: null, tags: [], steps: i ? [] : [{ type: 'key', value: 'Ctrl+C' }], ahk: '', ps: '', recipe: '', notes: '', testedAt: null })) }], activePageId: 'pg1', activeButtonIndex: null, panelVisibility: { console: true, library: true, profiles: true, llm: true } }], savedActions: [], runtime: 'ahk', filters: { search: '', tag: 'all' }, ollamaModel: 'qwen2.5-coder:latest' })); }
  }));
  p = await prof(page);
  ok('original-builder v3 save loads unchanged', p.name === 'Orig' && p.pages[0].buttons[0].label === 'X' && p.lp5Id === 'mx-main');
  ok('old Ollama model moves into AI settings, endpoint stays off', await page.evaluate(() => STATE.data.ai.model === 'qwen2.5-coder:latest' && STATE.data.ai.enabled === false && !('ollamaModel' in STATE.data)));
  ok('no JS errors on legacy loads', page.errors.length === 0, page.errors.join(' | '));
  await ctx.close();

  // A2: offline load + AI assist (copy/paste path, then mocked endpoints)
  ({ ctx, page } = await newPage(browser));
  page.on('dialog', d => d.accept());
  ok('js-yaml and ai-assist load from lib/, no network', await page.evaluate(() => typeof jsyaml === 'object' && typeof AiAssist === 'object') && page.netReqs.length === 0, page.netReqs.join(','));
  ok('AI endpoint off by default, Ask disabled', await page.evaluate(() => STATE.data.ai.enabled === false && STATE.data.ai.endpoint === '') && await page.isDisabled('#btn-llm-ask'));
  await page.fill('#llm-input', 'Save the file');
  await page.click('#btn-llm-build');
  const promptTxt = await page.inputValue('#llm-out');
  ok('Build Prompt gives a copyable key prompt', promptTxt.includes('Save the file') && promptTxt.includes('go_to_page') && promptTxt.includes('```yaml'));
  await cell(page, 4);
  await page.fill('#llm-answer', 'Sure! Here it is:\n```yaml\nlabel: "Save"\nsteps:\n  - key: "Ctrl+S"\n  - wait: 200\n```\nDone.');
  await page.click('#btn-llm-apply');
  p = await prof(page);
  ok('pasted answer applies to the selected button', p.pages[0].buttons[4].label === 'Save' && JSON.stringify(p.pages[0].buttons[4].steps) === JSON.stringify([{ type: 'key', value: 'Ctrl+S' }, { type: 'wait', value: '200' }]), JSON.stringify(p.pages[0].buttons[4].steps));
  await page.click('#kp-btn-undo');
  p = await prof(page); ok('AI apply can be undone', !p.pages[0].buttons[4].label);
  const SECRET2 = 'sk-' + 'abcdefghijklmnopqrstuvwx';
  await page.fill('#llm-answer', `label: "Bad"\nsteps:\n  - text: "${SECRET2}"`);
  await page.click('#btn-llm-apply');
  p = await prof(page); let st = await page.textContent('#llm-status');
  ok('answer with a secret is refused, value not echoed', !p.pages[0].buttons[4].label && /API key/.test(st) && !st.includes(SECRET2), st);
  await page.fill('#llm-answer', 'label: "X"\nsteps:\n  - launch: "calc"');
  await page.click('#btn-llm-apply');
  st = await page.textContent('#llm-status');
  ok('answer with an unknown step is refused', /unknown step 'launch'/.test(st), st);
  ok('copy/paste path made no network request', page.netReqs.length === 0, page.netReqs.join(','));
  let seen = null;
  const capture = (body) => async r => { seen = { url: r.request().url(), body: JSON.parse(r.request().postData() || '{}') }; await r.fulfill({ contentType: 'application/json', headers: { 'access-control-allow-origin': '*' }, body: JSON.stringify(body) }); };
  await ctx.route('http://localhost:8000/**', capture({ choices: [{ message: { content: '<think>hmm</think>\n```yaml\nlabel: "Term"\nsteps:\n  - open: "wt.exe"\n```' } }] }));
  await page.click('#llm-settings summary');
  await page.check('#llm-enabled');
  await page.fill('#llm-endpoint', 'http://localhost:8000/v3/chat/completions');
  await page.fill('#llm-model', 'qwen3-8b');
  ok('Ask enabled once the endpoint is on', !(await page.isDisabled('#btn-llm-ask')));
  await page.click('#btn-llm-ask');
  await page.waitForFunction(() => document.getElementById('llm-answer').value.includes('Term'), null, { timeout: 10000 });
  ok('OpenAI-compatible request shape', seen && seen.url === 'http://localhost:8000/v3/chat/completions' && seen.body.model === 'qwen3-8b' && seen.body.messages[0].content.includes('Save the file'), JSON.stringify(seen && { url: seen.url, model: seen.body.model }));
  await page.click('#btn-llm-apply');
  p = await prof(page); ok('endpoint answer applies (think block dropped)', p.pages[0].buttons[4].label === 'Term' && p.pages[0].buttons[4].steps[0].value === 'wt.exe');
  seen = null;
  await ctx.route('http://localhost:11434/**', capture({ response: 'label: "Olla"\nsteps:\n  - url: "https://ollama.com"' }));
  await page.fill('#llm-endpoint', 'http://localhost:11434');
  await page.click('#btn-llm-ask');
  await page.waitForFunction(() => document.getElementById('llm-answer').value.includes('Olla'), null, { timeout: 10000 });
  ok('Ollama base URL gets /api/generate with prompt', seen && seen.url === 'http://localhost:11434/api/generate' && typeof seen.body.prompt === 'string' && seen.body.stream === false, JSON.stringify(seen && { url: seen.url }));
  await ctx.unroute('http://localhost:8000/**');
  await ctx.route('http://localhost:8000/**', r => r.abort());
  await page.fill('#llm-endpoint', 'http://localhost:8000/v3/chat/completions');
  await page.click('#btn-llm-ask');
  await page.waitForFunction(() => document.getElementById('llm-status').classList.contains('err'), null, { timeout: 10000 });
  st = await page.textContent('#llm-status');
  ok('endpoint down: readable error, prompt kept for copying', /Could not reach/.test(st) && (await page.inputValue('#llm-out')).includes('Save the file'), st);
  await page.reload();
  ok('endpoint settings persist', await page.evaluate(() => STATE.data.ai.enabled === true && STATE.data.ai.model === 'qwen3-8b'));
  // The aborted request above logs "Failed to load resource" on purpose; anything else is a real error.
  const aiErrors = page.errors.filter(e => !/Failed to load resource/.test(e));
  ok('no JS errors in AI assist flow', aiErrors.length === 0, aiErrors.join(' | '));
  await ctx.close();
  await browser.close();
  console.log(results.join('\n'));
  console.log(`\n${results.filter(r => r.startsWith('PASS')).length}/${results.length} passed`);
  fs.rmSync(OUT, { recursive: true, force: true });
  if (results.some(r => r.startsWith('FAIL'))) process.exitCode = 1;
})().catch(e => { console.log(results.join('\n')); console.error('CRASH', e); process.exit(1); });
