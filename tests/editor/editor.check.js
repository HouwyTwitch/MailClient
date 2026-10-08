// Checks of the message editor page (src/MailClient.App/Assets/editor.html) in Chromium, the engine behind WebView2.
// Run: node tests/editor/editor.check.js   (needs Node.js and the "playwright" package; PLAYWRIGHT_MODULE may point to it)
const path = require('path');
const { pathToFileURL } = require('url');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const editorPage = pathToFileURL(path.join(__dirname, '..', '..', 'src', 'MailClient.App', 'Assets', 'editor.html')).href;
const assert = (c, m) => { if (!c) { console.log('FAIL:', m); process.exitCode = 1; } else console.log('ok  ', m); };
(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage();
  await page.addInitScript(() => { window.__msgs = []; window.chrome = { webview: { postMessage: m => window.__msgs.push(m) } }; });
  await page.goto(editorPage);
  const ev = (f, ...a) => page.evaluate(f, ...a);
  const select = async (text) => ev(t => {
    const ed = document.getElementById('ed');
    const walker = document.createTreeWalker(ed, NodeFilter.SHOW_TEXT);
    let n; while ((n = walker.nextNode())) { const i = n.textContent.indexOf(t); if (i >= 0) {
      const r = document.createRange(); r.setStart(n, i); r.setEnd(n, i + t.length);
      const s = getSelection(); s.removeAllRanges(); s.addRange(r); return true; } }
    return false; }, text);
  const lastState = async () => { await page.waitForTimeout(60); return ev(() => [...window.__msgs].reverse().find(m => m.type === 'state')); };

  // 1. base font + size on a selection
  await ev(() => { setBaseFont('Times New Roman', 12); setContent('<p>Hello world here</p>'); });
  await select('world');
  await ev(() => setFontSize(16));
  let html = await ev(() => getContent());
  assert(html.startsWith('<div style="font-family:\'Times New Roman\',sans-serif;font-size:12pt">'), 'wrapper carries base font: ' + html.slice(0, 80));
  assert(/<span style="font-size: 16pt;?">world<\/span>/.test(html), 'size 16pt on selection: ' + html);
  assert(!/<font/.test(html), 'no <font> markers left');
  let st = await lastState();
  assert(st && st.size === 16, 'state reports 16pt at selection: ' + JSON.stringify(st));

  // 2. font family on a selection
  await select('Hello');
  await ev(() => setFontFamily('Arial'));
  html = await ev(() => getContent());
  assert(/font-family: Arial;?">Hello</.test(html), 'Arial applied: ' + html);
  st = await lastState();
  assert(st.font === 'Arial', 'state reports Arial: ' + st.font);

  // 3. colour then automatic colour
  await select('here');
  await ev(() => setColor('#FF0000'));
  html = await ev(() => getContent());
  assert(/color: rgb\(255, 0, 0\)/.test(html), 'red applied');
  await select('here');
  await ev(() => setColor(null));
  html = await ev(() => getContent());
  assert(!/color:/.test(html.replace(/background-color/g, '')) && !/010203|rgb\(1, 2, 3\)/.test(html), 'colour removed: ' + html);

  // 4. highlight then none
  await select('here');
  await ev(() => setHighlight('#FFFF00'));
  html = await ev(() => getContent());
  assert(/background-color: rgb\(255, 255, 0\)/.test(html), 'highlight applied');
  await select('here');
  await ev(() => setHighlight(null));
  html = await ev(() => getContent());
  assert(!/background-color/.test(html) && !/rgb\(1, 2, 3\)/.test(html), 'highlight removed: ' + html);

  // 5. size at a caret, then typing
  await ev(() => { setContent('<p>Start</p>'); });
  await page.click('#ed');
  await page.keyboard.press('End');
  await ev(() => setFontSize(20));
  await page.keyboard.type(' big');
  html = await ev(() => getContent());
  assert(/<span style="font-size: 20pt;?">\s?big<\/span>|font-size: 20pt;?">&nbsp;big/.test(html) || /20pt[^>]*>[^<]*big/.test(html), 'typed text at caret gets 20pt: ' + html);
  assert(!/<font/.test(html), 'caret marker converted');

  // 6. reopened draft: whole document with our wrapper
  await ev(() => setContent('<html><head><meta charset="utf-8"><style>p{margin:0}</style></head><body><div style="font-family:Georgia,sans-serif;font-size:14pt"><p>Draft text</p></div></body></html>'));
  html = await ev(() => getContent());
  assert(html.startsWith('<div style="font-family:Georgia,sans-serif;font-size:14pt">'), 'draft font adopted: ' + html.slice(0, 70));
  assert((html.match(/font-family:Georgia/g) || []).length === 1, 'wrapper not nested');
  assert(/<style>p\{margin:0\}<\/style>/.test(html), 'draft <style> kept');
  await select('Draft');
  st = await lastState();
  assert(st.font === 'Georgia' && st.size === 14, 'state in draft: ' + st.font + ' ' + st.size);

  // 7. bold state + undo availability
  await ev(() => exec('bold'));
  st = await lastState();
  assert(st.bold === true && st.canUndo === true, 'bold reported, undo enabled');

  // 8. foreign content without wrapper keeps base font and gets wrapped once
  await ev(() => { setBaseFont('Calibri', 11); setContent('<p>Reply text</p>'); });
  html = await ev(() => getContent());
  assert(html === '<div style="font-family:Calibri,sans-serif;font-size:11pt"><p>Reply text</p></div>', 'plain content wrapped: ' + html);

  // 9. font family and colour at a caret apply to typed text
  await ev(() => { setContent('<p>Abc</p>'); });
  await page.click('#ed'); await page.keyboard.press('End');
  await ev(() => setFontFamily('Courier New'));
  await page.keyboard.type('xyz');
  html = await ev(() => getContent());
  assert(/Courier New[^>]*>xyz/.test(html), 'font at caret applies to typing: ' + html);
  await ev(() => setColor('#0070C0'));
  await page.keyboard.type('Q');
  html = await ev(() => getContent());
  assert(/rgb\(0, 112, 192\)[^>]*>Q/.test(html), 'colour at caret applies to typing: ' + html);

  // 10. size change then a second size change on the same text replaces (no nested sizes)
  await ev(() => { setContent('<p>one two</p>'); });
  await select('two'); await ev(() => setFontSize(18));
  await ev(() => setFontSize(24));
  html = await ev(() => getContent());
  assert(/24pt;?">two</.test(html) && !/18pt/.test(html), 'second size replaces first: ' + html);
  st = await lastState();
  assert(st.size === 24, 'state 24 after second change');

  // 11. undo after size change
  await ev(() => exec('undo'));
  html = await ev(() => getContent());
  assert(!/<span[^>]*><\/span>/.test(html) && /18pt;?">two</.test(html), 'undo restores 18pt, no empty spans: ' + html);

  // 12. signature: inserted after the text, replaced for another account, removed, left alone when absent
  await ev(() => { setBaseFont('Calibri', 11); setContent('<p><br></p><p><br></p><div id="mc-signature"><div>Иванов</div></div>'); });
  await ev(() => setSignature('<div><b>Петров</b></div>', true));
  html = await ev(() => getContent());
  assert(/<div id="mc-signature"><div><b>Петров<\/b><\/div><\/div><\/div>$/.test(html) && !/Иванов/.test(html), 'signature replaced: ' + html);
  await ev(() => setSignature(''));
  html = await ev(() => getContent());
  assert(!/mc-signature|Петров/.test(html), 'signature removed: ' + html);
  await ev(() => setSignature('<div>Сидоров</div>', true));
  html = await ev(() => getContent());
  assert(!/Сидоров/.test(html), 'no signature added when only replacing: ' + html);
  await ev(() => { setContent('<p>Текст ответа</p>'); setSignature('<div>Сидоров</div>'); });
  html = await ev(() => getContent());
  assert(/<p>Текст ответа<\/p><p><br><\/p><div id="mc-signature"><div>Сидоров<\/div><\/div>/.test(html), 'signature inserted after the text: ' + html);
  await ev(() => setSignature('<div>Сидоров</div>'));
  html = await ev(() => getContent());
  assert((html.match(/mc-signature/g) || []).length === 1, 'inserting again does not duplicate: ' + html);

  await browser.close();
})();
