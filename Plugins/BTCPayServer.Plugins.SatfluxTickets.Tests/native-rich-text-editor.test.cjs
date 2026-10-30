// Real-host regression, not a synthetic DOM. The caller provisions disposable legacy
// descriptions (browser247-mark, images/video and hostile attributes) and authenticates
// with a synthetic account. Missing fixtures fail setup rather than silently skipping.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SATFLUX_PLAYWRIGHT_MODULE || '@playwright/test');
let browser;
let stage = 'fixture setup';
(async () => {
    const urls = JSON.parse(process.env.NATIVE_EDITOR_TEST_URLS_JSON || '[]');
    assert.ok(urls.length > 0);
    const base = new URL(urls[0]).origin;
    for (const value of urls) {
        const url = new URL(value);
        assert.ok(['127.0.0.1', 'localhost', '[::1]'].includes(url.hostname));
        assert.equal(url.origin, base);
        assert.equal(url.username + url.password, '');
    }
    assert.ok(process.env.NATIVE_EDITOR_TEST_EMAIL && process.env.NATIVE_EDITOR_TEST_PASSWORD);
    browser = await chromium.launch({ executablePath: process.env.CHROME_BIN, headless: true,
        args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-background-networking'] });
    const context = await browser.newContext();
    await context.route('**/*', route => new URL(route.request().url()).origin === base
        ? route.continue() : route.abort());
    await context.addInitScript(() => { window.__browser247Bad = 0; });
    const page = await context.newPage();
    stage = 'fixture login';
    await page.goto(base + '/login', { waitUntil: 'domcontentloaded' });
    await page.locator('#Email').fill(process.env.NATIVE_EDITOR_TEST_EMAIL);
    await page.locator('#Password').fill(process.env.NATIVE_EDITOR_TEST_PASSWORD);
    await Promise.all([page.waitForURL(url => !url.pathname.startsWith('/login')),
        page.locator('#LoginButton').click()]);
    let checks = 0;
    for (let index = 0; index < urls.length; index++) {
        stage = 'native editor ' + (index + 1);
        const response = await page.goto(urls[index], { waitUntil: 'domcontentloaded' });
        assert.equal(response.status(), 200); checks++;
        await page.locator('.note-editor .note-editable').waitFor({ state: 'attached' });
        assert.equal(await page.locator('.note-editor').count(), 1); checks++;
        assert.equal(await page.locator('#Description').evaluate(el => el.classList.contains('rich-text-editor')), true); checks++;
        const editable = page.locator('.note-editable');
        assert.ok(await editable.locator('img').count()); checks++;
        assert.ok(await editable.locator('video').count()); checks++;
        assert.equal(await editable.locator('form,input,button,script,style,[data-evil],[onclick],[onerror]').count(), 0); checks++;
        assert.equal(await editable.locator('.browser247-mark').evaluate(el => el.style.position), ''); checks++;
        assert.ok(await editable.locator('.browser247-mark').evaluate(el => el.style.color)); checks++;
        assert.equal(await page.evaluate(() => window.__browser247Bad), 0); checks++;
        assert.equal(await page.locator('#Description').evaluate(el => /<(?:form|input|script|style)\b|data-evil|onerror|onclick|position\s*:/i.test(el.value)), false); checks++;
    }
    console.log('PASS: ' + checks + ' native editor assertions against actual plugin Razor pages');
})().catch(() => {
    // Do not log raw errors, fixture values, credentials or browser authentication state.
    console.error('FAIL: ' + stage + ' (no fixture/auth details logged)');
    process.exitCode = 1;
}).finally(async () => { if (browser) await browser.close(); });
