// Run with node --test; no package install, browser, credentials or network required.
const test = require('node:test');
const assert = require('node:assert/strict');
const { admissionKey, admissions, normalizedAmount, readableForeground } = require('../BTCPayServer.Plugins.SatfluxTickets/Resources/js/ticket-sales.js');

test('admission identity survives merchant reordering and quantity edits', () => {
    const a = { id: 'a' }, b = { id: 'b' };
    const quantities = new Map([['a', 2], ['b', 1]]);
    const original = admissions([a, b], quantities);
    const reordered = admissions([b, a], quantities);
    assert.equal(original[0].key, reordered[1].key);
    assert.equal(original[1].key, reordered[2].key);
    quantities.set('a', 1);
    assert.equal(admissions([b, a], quantities)[1].key, original[0].key);
});
test('composite admission keys do not collide with merchant-provided IDs', () => {
    assert.notEqual(admissionKey('a:1', 0), admissionKey('a', 10));
    assert.notEqual(admissionKey('a"],0', 1), admissionKey('a', 0));
});
test('invalid counts cannot generate unbounded recipient fields', () => {
    for (const quantity of [-1, 1.5, 11, Infinity, NaN])
        assert.deepEqual(admissions([{ id: 'a' }], new Map([['a', quantity]])), []);
    assert.equal(admissions([{ id: 'a' }], new Map([['a', 10]])).length, 10);
});

test('display follows host rounding without hiding very small prices', () => {
    assert.deepEqual(normalizedAmount(12.345, 2), { value: 12.35, precision: 2 });
    assert.deepEqual(normalizedAmount(0.001, 2), { value: 0.001, precision: 3 });
    assert.deepEqual(normalizedAmount(0.00000001, 8), { value: 0.00000001, precision: 8 });
    assert.deepEqual(normalizedAmount(100.005, 2), { value: 100.01, precision: 2 });
});

test('merchant action foreground adapts to light and dark brand colors', () => {
    assert.equal(readableForeground(81, 177, 62), '#000');
    assert.equal(readableForeground(0, 34, 85), '#fff');
    assert.equal(readableForeground(255, 255, 255), '#000');
    assert.equal(readableForeground(0, 0, 0), '#fff');
});

const { localizedDate } = require('../BTCPayServer.Plugins.SatfluxTickets/Resources/js/ticket-sales.js');
test('event UTC instants use the viewer timezone and its event-date DST, never a fixed CET offset', () => {
    const winter = localizedDate('2026-11-28T08:00:00.0000000Z', 'sk', 'Europe/Bratislava');
    const summer = localizedDate('2026-06-28T08:00:00.0000000Z', 'sk', 'Europe/Bratislava');
    assert.equal(winter.timeZone, 'Europe/Bratislava');
    assert.match(winter.text, /09:00/);
    assert.match(summer.text, /10:00/);
    assert.match(localizedDate('2026-11-28T08:00:00Z', 'en', 'America/New_York').text, /03:00|3:00/);
    assert.match(localizedDate('2026-11-28T08:00:00Z', 'en', 'UTC').text, /08:00|8:00/);
});
test('invalid dates, unsupported Intl options and already-local values preserve the server UTC fallback', () => {
    for (const date of ['not-a-date', '2026-11-28T09:00:00+01:00', '2026-11-28 08:00', '9999-99-99T00:00:00Z', null])
        assert.equal(localizedDate(date, 'sk', 'Europe/Bratislava'), null);
    assert.equal(localizedDate('2026-11-28T08:00:00Z', 'sk', 'invalid/timezone'), null);
    const original = Intl.DateTimeFormat;
    try {
        Intl.DateTimeFormat = undefined;
        assert.equal(localizedDate('2026-11-28T08:00:00Z', 'sk'), null);
    } finally { Intl.DateTimeFormat = original; }
});


const { translation, translateNodes, mount } = require('../BTCPayServer.Plugins.SatfluxTickets/Resources/js/ticket-sales.js');
test('instant language switching accepts only the five complete constant dictionaries', () => {
    const config = { copy: { Ticket: 'Ticket', Email: 'Email' }, translations: { sk: { Ticket: 'Lístok', Email: 'E-mail' } } };
    assert.equal(translation(config, 'sk').Ticket, 'Lístok');
    for (const language of ['fr', 'SK', '<script>', '__proto__', 'constructor']) assert.equal(translation(config, language), null);
    config.translations.de = { Ticket: 'Ticket' };
    config.translations.es = { Ticket: 'Entrada', Email: { html: '<script>' } };
    assert.equal(translation(config, 'de'), null);
    assert.equal(translation(config, 'es'), null);
});
test('translation replaces only presentation text and aria, preserving live recipient input identity and incomplete values', () => {
    const label = { dataset: { salesCopy: 'Email' }, textContent: 'Email address' };
    const aria = { dataset: { salesCopyAria: 'Enlarge' }, setAttribute(name, value) { this[name] = value; } };
    const heading = { dataset: { salesRecipientNumber: '2', salesRecipientType: 'Merchant <strong>name</strong>' }, textContent: '' };
    const input = { value: 'incomplete@', name: 'ContactInfo[1].Email', selectionStart: 5 };
    const nonce = { value: 'same-synthetic-nonce' };
    const radio = { checked: true };
    const page = { input, nonce, radio, querySelectorAll(selector) {
        return ({ '[data-sales-copy]': [label], '[data-sales-copy-aria]': [aria], '[data-sales-recipient-number]': [heading] })[selector] || [];
    } };
    translateNodes(page, { Email: 'E-mailová adresa', Enlarge: 'Zväčšiť obrázok', Ticket: 'Lístok' });
    assert.equal(label.textContent, 'E-mailová adresa');
    assert.equal(aria['aria-label'], 'Zväčšiť obrázok');
    assert.equal(heading.textContent, 'Lístok 2 · Merchant <strong>name</strong>');
    assert.equal(page.input, input); assert.equal(input.value, 'incomplete@'); assert.equal(input.selectionStart, 5);
    assert.equal(page.nonce, nonce); assert.equal(nonce.value, 'same-synthetic-nonce'); assert.equal(radio.checked, true);
});
test('initialization failure is visible and repeat mounting cannot attach duplicate handlers', () => {
    const diagnostic = { hidden: true }; let attempts = 0;
    const element = { dataset: {}, querySelector(selector) {
        if (selector === '[data-sales-startup-error]') return diagnostic;
        attempts++; throw new Error('synthetic configuration failure');
    } };
    const original = console.error; const messages = [];
    try {
        console.error = message => messages.push(message);
        mount(element); mount(element);
    } finally { console.error = original; }
    assert.equal(element.dataset.salesInitialized, 'failed'); assert.equal(diagnostic.hidden, false);
    assert.equal(attempts, 1); assert.equal(messages.length, 1); assert.match(messages[0], /initialization failed/);
    assert.doesNotMatch(messages[0], /synthetic configuration/);
});


const { renderTimes } = require('../BTCPayServer.Plugins.SatfluxTickets/Resources/js/ticket-sales.js');
const { normalizeEditorZone } = require('../BTCPayServer.Plugins.SatfluxTickets/Resources/js/ticket-event-editor.js');
test('physical venue times use server strings even when browser Intl is unavailable or the viewer is elsewhere', () => {
    const start = { dataset: { salesTimePart: 'start' }, textContent: '08:00 UTC' };
    const end = { dataset: { salesTimePart: 'end' }, textContent: '11:00 UTC' };
    const note = { textContent: '', hidden: true };
    const page = { querySelectorAll: () => [start, end], querySelector: () => note };
    const config = { timeMode: 'venue', language: 'sk', venueDates: { sk: { start: '09:00 (UTC+01:00, Europe/Bratislava)', end: '12:00 (UTC+01:00, Europe/Bratislava)', label: 'Časové pásmo miesta konania: Europe/Bratislava' } } };
    const original = Intl.DateTimeFormat;
    try {
        Intl.DateTimeFormat = () => { throw new Error('outdated visitor browser'); };
        renderTimes(page, config, { YourTimezone: 'Tvoje časové pásmo' });
    } finally { Intl.DateTimeFormat = original; }
    assert.equal(start.textContent, config.venueDates.sk.start); assert.equal(end.textContent, config.venueDates.sk.end);
    assert.equal(note.textContent, config.venueDates.sk.label); assert.equal(note.hidden, false);
});
test('physical events without a venue zone keep explicit UTC instead of using the visitor zone', () => {
    const start = { dataset: { salesTimePart: 'start' }, textContent: '08:00 UTC' };
    const note = { hidden: true };
    const page = { querySelectorAll: () => [start], querySelector: () => note };
    renderTimes(page, { timeMode: 'venue', language: 'en', venueDates: {} }, {});
    assert.equal(start.textContent, '08:00 UTC'); assert.equal(note.hidden, true);
});
test('native virtual editor normalizes only known checked IANA renames and clearly falls back to UTC', () => {
    const catalog = ['UTC', 'Asia/Kolkata', 'Europe/Kyiv', 'Asia/Kathmandu', 'Europe/Bratislava'];
    assert.equal(normalizeEditorZone('Asia/Calcutta', catalog), 'Asia/Kolkata');
    assert.equal(normalizeEditorZone('Europe/Kiev', catalog), 'Europe/Kyiv');
    assert.equal(normalizeEditorZone('Asia/Katmandu', catalog), 'Asia/Kathmandu');
    assert.equal(normalizeEditorZone('Europe/Bratislava', catalog), 'Europe/Bratislava');
    assert.equal(normalizeEditorZone('unknown/browser-zone', catalog), 'UTC');
    assert.equal(normalizeEditorZone('Asia/Calcutta', ['UTC']), 'UTC');
    assert.equal(normalizeEditorZone('CET', catalog), 'UTC');
});
