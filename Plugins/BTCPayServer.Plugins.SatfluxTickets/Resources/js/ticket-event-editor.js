/* Authoring preview only. The provider validates IANA zones and converts civil times to canonical UTC. */
(function () {
    'use strict';
    function normalizeEditorZone(browser, catalog) {
        if (catalog.includes(browser)) return browser;
        const aliases = { 'Asia/Calcutta': 'Asia/Kolkata', 'Europe/Kiev': 'Europe/Kyiv', 'Asia/Katmandu': 'Asia/Kathmandu' };
        const canonical = Object.prototype.hasOwnProperty.call(aliases, browser) ? aliases[browser] : null;
        return canonical && catalog.includes(canonical) ? canonical : 'UTC';
    }
    if (typeof module === 'object' && module.exports) module.exports = { normalizeEditorZone };
    if (typeof document === 'undefined') return;
    function start() {
        document.querySelectorAll('[data-ticket-event-editor]').forEach(form => {
            if (form.dataset.eventEditorReady) return;
            form.dataset.eventEditorReady = 'true';
            const config = JSON.parse(form.querySelector('[data-event-editor-configuration]').textContent);
            const type = form.querySelector('[name="EventType"]');
            const venue = form.querySelector('[name="TimeZone"]');
            const editor = form.querySelector('[name="EditorTimeZone"]');
            const note = form.querySelector('[data-event-editor-zone]');
            const fields = ['Start', 'End'].map(name => ({
                name, input: form.querySelector('[name="' + name + 'Local"]'),
                edited: form.querySelector('[name="' + name + 'Edited"]'),
                instant: config[name.toLowerCase() + 'Utc']
            }));
            fields.forEach(field => {
                const markEdited = () => { field.edited.value = 'true'; };
                field.input.addEventListener('input', markEdited);
                field.input.closest('.input-group')?.querySelector('.input-group-clear')?.addEventListener('click', markEdited);
            });
            // UTC is the visible, safe default before JavaScript. Never persist this virtual editor preference.
            try {
                const browser = Intl.DateTimeFormat().resolvedOptions().timeZone;
                editor.value = normalizeEditorZone(browser, config.timeZones);
            } catch (_) { editor.value = 'UTC'; }
            function update() {
                const physical = type.value === 'Physical' || type.value === '1';
                form.querySelector('[data-event-venue-zone]').hidden = !physical;
                venue.required = physical;
                const zone = physical ? venue.value : editor.value;
                note.textContent = zone || 'UTC';
                if (!config.timeZones.includes(zone)) return;
                fields.forEach(field => {
                    if (config.preserveDraft || field.edited.value.toLowerCase() === 'true' || !field.instant) return;
                    try {
                        const parts = new Intl.DateTimeFormat('en-GB', { timeZone: zone, year: 'numeric', month: '2-digit', day: '2-digit',
                            hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23' }).formatToParts(new Date(field.instant));
                        const values = Object.fromEntries(parts.map(part => [part.type, part.value]));
                        field.input.value = values.year + '-' + values.month + '-' + values.day + 'T' + values.hour + ':' + values.minute + ':' + values.second;
                    } catch (_) { /* Unchanged values remain bound to the original instant by the server. */ }
                });
            }
            type.addEventListener('change', update);
            venue.addEventListener('change', update);
            update();
        });
    }
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
})();
