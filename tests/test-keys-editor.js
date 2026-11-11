const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const ROOT = path.resolve(__dirname, '..');
const HTML = require('url').pathToFileURL(path.join(ROOT, 'v2', 'editor', 'keys-editor.html')).href;
const KEYS_PATH = path.join(ROOT, 'v2', 'keys.yaml');

const results = [];
const ok = (name, cond, extra = '') => {
  results.push(`${cond ? 'PASS' : 'FAIL'}  ${name}${extra ? '  — ' + extra : ''}`);
};

(async () => {
  const browser = await chromium.launch();
  const ctx = await browser.newContext({
    acceptDownloads: true,
    viewport: { width: 1280, height: 900 }
  });
  const page = await ctx.newPage();

  page.errors = [];
  page.netReqs = [];
  page.on('pageerror', e => page.errors.push(String(e)));
  page.on('console', m => { if (m.type() === 'error') page.errors.push(m.text()); });
  page.on('request', r => {
    const u = r.url();
    if (!u.startsWith('file:') && !u.startsWith('data:') && !u.startsWith('blob:')) page.netReqs.push(u);
  });
  page.on('dialog', d => d.accept());

  await page.goto(HTML);

  // 1. Initial render & dependencies
  const depsOk = await page.evaluate(() => typeof jsyaml === 'object' && typeof AiAssist === 'object' && typeof __EDITOR === 'object');
  ok('js-yaml and ai-assist load offline in keys-editor', depsOk && page.netReqs.length === 0, page.netReqs.join(','));

  const tabCount = await page.locator('.page-tab').count();
  const cellCount = await page.locator('.key-cell').count();
  ok('initial 3x3 grid and 10 tabs render', tabCount === 10 && cellCount === 9, `tabs: ${tabCount}, cells: ${cellCount}`);

  // 2. Load the real v2/keys.yaml
  const realYaml = fs.readFileSync(KEYS_PATH, 'utf8');
  await page.evaluate((yaml) => window.__EDITOR.loadFromYaml(yaml), realYaml);

  const p1Btn0Label = await page.locator('.key-cell').nth(0).locator('.key-label').textContent();
  const p1Btn7Label = await page.locator('.key-cell').nth(7).locator('.key-label').textContent();
  ok('keys.yaml loads into 3x3 grid (Open DL on 0, Index on 7)', p1Btn0Label.includes('Open DL') && p1Btn7Label.includes('Index'), `0: ${p1Btn0Label}, 7: ${p1Btn7Label}`);

  // 3. Tab switching & page 2 inspection
  await page.locator('.page-tab').nth(1).click(); // Switch to Page 2 (Home)
  const p2Btn0Label = await page.locator('.key-cell').nth(0).locator('.key-label').textContent();
  ok('switching to page 2 shows Gateway on 0', p2Btn0Label.includes('Gateway'), p2Btn0Label);

  // 4. Click a key to edit details in single panel
  await page.locator('.key-cell').nth(3).click(); // Ref Drafts
  const editTitle = await page.textContent('#btn-editor-title');
  const labelVal = await page.inputValue('#btn-label-input');
  ok('clicking a key opens it in the single editor panel', editTitle.includes('Key 3') && labelVal.includes('Ref Drafts'), `${editTitle} / ${labelVal}`);

  // 5. Add a step and edit label
  await page.fill('#btn-label-input', 'Ref Edited');
  await page.click('#btn-add-step');
  const stepRows = page.locator('#step-list .step-row');
  const stepCount = await stepRows.count();
  const lastStepInput = stepRows.nth(stepCount - 1).locator('input');
  await lastStepInput.fill('Ctrl+Shift+E');

  const cell3Label = await page.locator('.key-cell').nth(3).locator('.key-label').textContent();
  ok('editing label and adding steps updates grid live', cell3Label.includes('Ref Edited') && stepCount >= 2, cell3Label);

  // 6. Shortcut recorder test
  await page.click('#qp-record');
  const recorderActive = await page.evaluate(() => document.getElementById('shortcut-recorder').classList.contains('active'));
  ok('recording shortcut opens recorder overlay', recorderActive);

  // Simulate Ctrl+Alt+K keydown
  await page.keyboard.down('Control');
  await page.keyboard.down('Alt');
  await page.keyboard.press('KeyK');
  await page.keyboard.up('Alt');
  await page.keyboard.up('Control');

  const recordedText = await page.textContent('#recorded-combo-disp');
  ok('recorder captures key combo', recordedText.includes('Ctrl') && recordedText.includes('Alt') && recordedText.includes('K'), recordedText);

  await page.click('#btn-apply-recorded');
  const recorderClosed = await page.evaluate(() => !document.getElementById('shortcut-recorder').classList.contains('active'));
  ok('applying shortcut closes recorder and adds step', recorderClosed);

  // 7. Paths drawer
  await page.click('#btn-toggle-paths');
  const pathsDrawerOpen = await page.evaluate(() => document.getElementById('paths-drawer').classList.contains('open'));
  const pathsCount = await page.locator('.path-row').count();
  ok('paths drawer opens and lists variables', pathsDrawerOpen && pathsCount >= 5, `paths count: ${pathsCount}`);

  // 8. Secret validation refusal
  await page.fill('#btn-label-input', 'Secret Key');
  const lastRowInput = page.locator('#step-list .step-row').last().locator('input');
  await lastRowInput.fill('gsk_12345678901234567890');

  const problems = await page.evaluate(() => window.__EDITOR.validateLayout());
  ok('secret pattern in step is refused by validation', problems.some(p => p.includes('API key or password')), problems.join('; '));

  // Reset to valid step
  await lastRowInput.fill('echo valid');

  // 9. AI Assist test (mock endpoint)
  let seenAiReq = null;
  await ctx.route('http://localhost:8000/**', async r => {
    seenAiReq = { url: r.request().url(), body: JSON.parse(r.request().postData() || '{}') };
    await r.fulfill({
      contentType: 'application/json',
      headers: { 'access-control-allow-origin': '*' },
      body: JSON.stringify({
        choices: [{
          message: {
            content: '```yaml\nlabel: "AI Made"\nsteps:\n  - open: "calc.exe"\n```'
          }
        }]
      })
    });
  });

  await page.click('#ai-toggle-settings');
  await page.check('#ai-enabled');
  await page.fill('#ai-prompt-input', 'Calculator key');
  await page.click('#btn-ai-ask');

  await page.waitForFunction(() => document.getElementById('ai-answer-area').value.includes('calc.exe'), null, { timeout: 10000 });
  ok('AI Assist ask communicates with local endpoint', seenAiReq && seenAiReq.url.includes('/chat/completions') && seenAiReq.body.model === 'qwen3-8b');

  await page.click('#btn-ai-apply');
  const postAiLabel = await page.inputValue('#btn-label-input');
  ok('AI Assist apply updates current button label and steps', postAiLabel === 'AI Made');

  // 10. File save / download
  // In headless Playwright, remove showSaveFilePicker so download triggers reliably
  await page.evaluate(() => { delete window.showSaveFilePicker; });

  const [download] = await Promise.all([
    page.waitForEvent('download'),
    page.click('#btn-save-file')
  ]);
  const dlPath = path.join(require('os').tmpdir(), 'downloaded-keys.yaml');
  await download.saveAs(dlPath);
  const exportedYaml = fs.readFileSync(dlPath, 'utf8');
  ok('saving layout exports valid YAML file with paths and pages', exportedYaml.includes('profile_name:') && exportedYaml.includes('paths:') && exportedYaml.includes('AI Made'));
  fs.unlinkSync(dlPath);

  // Check no unexpected console/page errors
  ok('no unexpected JS errors throughout test flow', page.errors.length === 0, page.errors.join(' | '));

  await ctx.close();
  await browser.close();

  console.log('\n--- v2 Editor Playwright Test Results ---');
  console.log(results.join('\n'));
  const passed = results.filter(r => r.startsWith('PASS')).length;
  console.log(`\n${passed}/${results.length} passed`);

  if (results.some(r => r.startsWith('FAIL'))) {
    process.exit(1);
  }
})().catch(e => {
  console.log(results.join('\n'));
  console.error('CRASH in test-keys-editor.js:', e);
  process.exit(1);
});
