/* Public sales only. No browser persistence, payment authority or merchant API credentials. */
(function (root, factory) {
    'use strict';
    const api = factory();
    if (typeof module === 'object' && module.exports) module.exports = api;
    if (root.document) {
        const start = () => root.document.querySelectorAll('[data-ticket-sales]').forEach(api.mount);
        if (root.document.readyState === 'loading') root.document.addEventListener('DOMContentLoaded', start, { once: true });
        else start();
    }
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
    'use strict';
    const admissionKey = (typeId, index) => JSON.stringify([typeId, index]);
    function admissions(types, quantities) {
        const result = [];
        types.forEach(type => {
            const quantity = quantities.get(type.id) || 0;
            if (!Number.isInteger(quantity) || quantity < 0 || quantity > 10) return;
            for (let index = 0; index < quantity && result.length < 100; index++) result.push({ type, index, key: admissionKey(type.id, index) });
        });
        return result;
    }
    // Match BTCPay Rating.Extensions.RoundToSignificant for positive display totals.
    // The server's decimal calculation remains authoritative for invoice creation.
    function normalizedAmount(amount, decimals) {
        let precision = Math.max(0, Math.min(28, decimals));
        if (!Number.isFinite(amount) || amount === 0) return { value: 0, precision };
        while (precision < 28) {
            const factor = 10 ** precision;
            const rounded = Math.round((amount + Number.EPSILON * Math.abs(amount)) * factor) / factor;
            if (Math.abs((rounded - amount) / amount) < 0.01) return { value: rounded, precision };
            precision++;
        }
        return { value: amount, precision };
    }
    function readableForeground(red, green, blue) {
        const linear = channel => { const value = channel / 255; return value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4; };
        const luminance = 0.2126 * linear(red) + 0.7152 * linear(green) + 0.0722 * linear(blue);
        return (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) ? '#000' : '#fff';
    }
    function localizedDate(isoUtc, language, timeZone) {
        // The source is always the server's UTC instant, never an already-formatted local date.
        // Failure leaves the explicit UTC server fallback intact, including without JavaScript.
        if (typeof isoUtc !== 'string' || !/^\d{4}-\d{2}-\d{2}T.*Z$/.test(isoUtc)) return null;
        const instant = new Date(isoUtc);
        if (!Number.isFinite(instant.getTime())) return null;
        try {
            const formatter = new Intl.DateTimeFormat(language, {
                year: 'numeric', month: 'long', day: 'numeric', weekday: 'long',
                hour: '2-digit', minute: '2-digit', timeZoneName: 'short',
                ...(timeZone ? { timeZone } : {})
            });
            return { text: formatter.format(instant), timeZone: formatter.resolvedOptions().timeZone };
        } catch (_) { return null; }
    }
    function mount(element) {
        if (element.dataset.salesInitialized) return;
        element.dataset.salesInitialized = 'starting';
        try {
            initialize(element);
            element.dataset.salesInitialized = 'ready';
            const diagnostic = element.querySelector('[data-sales-startup-error]');
            if (diagnostic) diagnostic.hidden = true;
        } catch (_) {
            element.dataset.salesInitialized = 'failed';
            const diagnostic = element.querySelector('[data-sales-startup-error]');
            if (diagnostic) diagnostic.hidden = false;
            // Never include JSON drafts, recipient details or exception contents in diagnostics.
            console.error('Tickets sales initialization failed. Reload the page or contact the organiser.');
        }
    }
    function translation(config, language) {
        if (!['en', 'sk', 'es', 'de', 'cs'].includes(language)
            || !Object.prototype.hasOwnProperty.call(config.translations || {}, language)) return null;
        const candidate = config.translations[language];
        if (!candidate || typeof candidate !== 'object' || Array.isArray(candidate)
            || Object.keys(config.copy).some(key => !Object.prototype.hasOwnProperty.call(candidate, key) || typeof candidate[key] !== 'string')) return null;
        return candidate;
    }
    function translateNodes(element, copy) {
        element.querySelectorAll('[data-sales-copy]').forEach(node => {
            const key = node.dataset.salesCopy;
            if (typeof copy[key] === 'string') node.textContent = copy[key];
        });
        element.querySelectorAll('[data-sales-copy-aria]').forEach(node => {
            const key = node.dataset.salesCopyAria;
            if (typeof copy[key] === 'string') node.setAttribute('aria-label', copy[key]);
        });
        element.querySelectorAll('[data-sales-recipient-number]').forEach(node => {
            node.textContent = copy.Ticket + ' ' + node.dataset.salesRecipientNumber + ' · ' + node.dataset.salesRecipientType;
        });
    }
    function renderTimes(element, config, copy) {
        if (config.timeMode === 'venue') {
            const venue = config.venueDates && config.venueDates[config.language];
            if (!venue) return; // Unknown physical zone keeps the server's explicit UTC warning.
            element.querySelectorAll('[data-sales-local-time]').forEach(time => {
                const value = venue[time.dataset.salesTimePart];
                if (typeof value === 'string') time.textContent = value;
            });
            const note = element.querySelector('[data-sales-timezone]');
            if (note && typeof venue.label === 'string') { note.textContent = venue.label; note.hidden = false; }
            return;
        }
        let displayedTimezone;
        element.querySelectorAll('[data-sales-local-time]').forEach(time => {
            const localized = localizedDate(time.getAttribute('datetime'), config.language);
            if (!localized) return;
            time.textContent = localized.text;
            displayedTimezone = localized.timeZone;
        });
        const timezoneNote = element.querySelector('[data-sales-timezone]');
        if (displayedTimezone && timezoneNote) {
            timezoneNote.textContent = copy.YourTimezone + ': ' + displayedTimezone;
            timezoneNote.hidden = false;
        }
    }
    function initialize(element) {
        const config = JSON.parse(element.querySelector('[data-sales-configuration]').textContent);
        // Keep merchant branding and choose a foreground with at least 4.5:1 contrast on its solid accent.
        const swatch = document.createElement('span'); swatch.hidden = true; swatch.style.color = 'var(--sales-accent)'; element.append(swatch);
        const accent = getComputedStyle(swatch).color;
        const channels = accent.match(/^rgb\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)\s*\)$/);
        if (channels) {
            element.style.setProperty('--sales-action-bg', accent);
            element.style.setProperty('--sales-action-text', readableForeground(Number(channels[1]), Number(channels[2]), Number(channels[3])));
        }
        swatch.remove();
        const form = element.querySelector('[data-sales-form]');
        let copy = config.copy;
        function localizeTimes() { renderTimes(element, config, copy); }
        localizeTimes();
        const money = amount => {
            const normalized = normalizedAmount(amount, config.decimals);
            return new Intl.NumberFormat(config.language, {
                minimumFractionDigits: normalized.precision, maximumFractionDigits: normalized.precision
            }).format(normalized.value) + ' ' + config.currency;
        };
        const quantities = new Map();
        const drafts = new Map((config.contacts || []).map(c => [admissionKey(c.typeId, c.admissionIndex), {
            firstName: c.firstName || '', lastName: c.lastName || '', email: c.email || ''
        }]));
        const controls = new Map();
        const recipients = element.querySelector('[data-sales-recipients]');
        const contacts = element.querySelector('[data-sales-contacts]');
        // Reposition the same summary node; never duplicate fields, IDs or live regions at a breakpoint.
        const summaryPanel = element.querySelector('.sales-summary');
        const desktopSummaryPosition = document.createComment('sales-summary-desktop-position');
        summaryPanel.before(desktopSummaryPosition);
        const mobileLayout = window.matchMedia('(max-width: 760px)');
        function placeSummary() {
            if (mobileLayout.matches) contacts.before(summaryPanel);
            else desktopSummaryPosition.after(summaryPanel);
        }
        mobileLayout.addEventListener('change', placeSummary);
        placeSummary();
        const shared = element.querySelector('[data-sales-shared-email]');
        const sharedInput = shared.querySelector('input');
        const primary = Array.from(element.querySelectorAll('[data-sales-primary]'));
        const live = element.querySelector('[data-sales-live]');
        let contactsOpen = config.contactsOpen;
        let submitting = false;
        const individual = () => form.querySelector('[name="DeliveryOption"][value="individual"]').checked;
        function remember() {
            recipients.querySelectorAll('[data-admission-key]').forEach(group => {
                const value = {};
                group.querySelectorAll('[data-contact-field]').forEach(input => value[input.dataset.contactField] = input.value);
                drafts.set(group.dataset.admissionKey, value);
            });
        }
        function field(group, index, name, labelText, value, type, key, autocomplete) {
            const wrapper = document.createElement('div');
            wrapper.className = 'sales-field';
            const label = document.createElement('label');
            const input = document.createElement('input');
            input.id = 'sales-contact-' + index + '-' + name;
            input.name = 'ContactInfo[' + index + '].' + name;
            input.type = type;
            input.className = 'form-control';
            input.value = value || '';
            input.maxLength = type === 'email' ? 254 : 100;
            input.autocomplete = autocomplete;
            input.dataset.contactField = key;
            input.readOnly = config.frozen;
            input.required = contactsOpen && (type !== 'email' || individual());
            label.htmlFor = input.id;
            label.textContent = labelText;
            label.dataset.salesCopy = name;
            wrapper.append(label, input);
            if (type === 'email') wrapper.hidden = !individual();
            group.append(wrapper);
        }
        function renderRecipients() {
            remember();
            recipients.replaceChildren();
            const items = admissions(config.types, quantities);
            items.forEach((item, position) => {
                const draft = drafts.get(item.key) || {};
                const group = document.createElement('section');
                group.className = 'sales-recipient';
                group.dataset.admissionKey = item.key;
                const heading = document.createElement('h3');
                heading.textContent = copy.Ticket + ' ' + (position + 1) + ' · ' + item.type.name;
                heading.dataset.salesRecipientNumber = String(position + 1);
                heading.dataset.salesRecipientType = item.type.name;
                group.append(heading);
                [['TicketTypeId', item.type.id], ['AdmissionIndex', item.index]].forEach(([name, value]) => {
                    const input = document.createElement('input');
                    input.type = 'hidden'; input.name = 'ContactInfo[' + position + '].' + name; input.value = value;
                    group.append(input);
                });
                const names = document.createElement('div');
                names.className = 'sales-name-fields';
                field(names, position, 'FirstName', copy.FirstName, draft.firstName, 'text', 'firstName', 'given-name');
                field(names, position, 'LastName', copy.LastName, draft.lastName, 'text', 'lastName', 'family-name');
                group.append(names);
                field(group, position, 'Email', copy.Email, draft.email, 'email', 'email', 'email');
                recipients.append(group);
            });
            shared.hidden = individual();
            sharedInput.required = contactsOpen && !individual();
            contacts.hidden = !contactsOpen || items.length === 0;
        }
        function state() {
            let total = 0, count = 0, valid = true;
            config.types.forEach(type => {
                const control = controls.get(type.id);
                const quantity = quantities.get(type.id) || 0;
                valid = valid && control.input.checkValidity() && Number.isInteger(quantity);
                count += quantity;
                total += quantity * type.price;
            });
            return { total, count, valid: valid && count > 0 && count <= 100 };
        }
        function update() {
            const current = state();
            config.types.forEach(type => {
                const { row, input, minus, plus } = controls.get(type.id);
                const quantity = quantities.get(type.id) || 0;
                row.classList.toggle('is-selected', quantity > 0);
                minus.disabled = config.frozen || quantity <= 0;
                plus.disabled = config.frozen || quantity >= type.maximum || current.count >= 100;
                input.setAttribute('aria-invalid', String(!input.checkValidity()));
            });
            const summary = element.querySelector('[data-sales-summary]');
            summary.replaceChildren();
            let hasSelection = false;
            config.types.forEach(type => {
                const quantity = quantities.get(type.id) || 0;
                if (quantity <= 0) return;
                hasSelection = true;
                const row = document.createElement('div'); row.className = 'sales-summary-line';
                const label = document.createElement('span'); label.textContent = quantity + ' × ' + type.name;
                const amount = document.createElement('strong'); amount.textContent = money(type.price * quantity);
                row.append(label, amount); summary.append(row);
            });
            if (!hasSelection) { const empty = document.createElement('p'); empty.className = 'sales-muted'; empty.textContent = copy.NoTickets; summary.append(empty); }
            element.querySelectorAll('[data-sales-total]').forEach(total => total.textContent = money(current.total));
            primary.forEach(button => {
                button.disabled = submitting || !current.valid || current.total <= 0;
                button.textContent = submitting ? copy.Working : config.frozen && !config.checkoutUrl ? copy.VerifyPayment : contactsOpen || config.frozen ? copy.Pay : copy.Continue;
            });
            element.querySelector('[data-sales-zero]').hidden = current.count === 0 || current.total > 0;
            live.textContent = copy.TicketCount + ': ' + current.count + '. ' + copy.Total + ': ' + money(current.total);
        }
        const typeRows = new Map(Array.from(element.querySelectorAll('[data-sales-type]')).map(row => [row.dataset.salesType, row]));
        config.types.forEach(type => {
            const row = typeRows.get(type.id);
            const input = row.querySelector('[data-sales-quantity]');
            const minus = row.querySelector('[data-sales-minus]');
            const plus = row.querySelector('[data-sales-plus]');
            controls.set(type.id, { row, input, minus, plus });
            quantities.set(type.id, Number(input.value));
            function changed() {
                quantities.set(type.id, input.value === '' ? 0 : Number(input.value));
                renderRecipients(); update();
            }
            input.addEventListener('input', changed);
            minus.addEventListener('click', () => { if (!config.frozen) { input.value = Math.max(0, (quantities.get(type.id) || 0) - 1); changed(); } });
            plus.addEventListener('click', () => { if (!config.frozen) { input.value = Math.min(type.maximum, (quantities.get(type.id) || 0) + 1); changed(); } });
        });
        const languageSelect = element.querySelector('[data-sales-language]');
        function changeLanguage(language) {
            const selected = translation(config, language);
            if (!selected || submitting) { if (languageSelect) languageSelect.value = config.language; return; }
            config.language = language;
            copy = selected;
            // Translate presentation nodes only; never replace recipient inputs, merchant copy or forms.
            translateNodes(element, copy);
            config.types.forEach(type => {
                const { row, minus, plus } = controls.get(type.id);
                minus.setAttribute('aria-label', copy.Decrease + ': ' + type.name);
                plus.setAttribute('aria-label', copy.Increase + ': ' + type.name);
                row.querySelector('.sales-quantity label').textContent = copy.Quantity + ': ' + type.name;
                row.querySelector('.sales-price').textContent = money(type.price);
            });
            const languageField = form.querySelector('[data-sales-language-field]');
            if (languageField) languageField.value = language;
            document.documentElement.lang = language;
            localizeTimes(); update();
            // Make the chosen locale shareable without navigation or adding any recipient data.
            const url = new URL(window.location.href);
            url.searchParams.set('lang', language);
            window.history.replaceState(window.history.state, '', url);
        }
        if (languageSelect) languageSelect.addEventListener('change', () => changeLanguage(languageSelect.value));
        form.querySelectorAll('[name="DeliveryOption"]').forEach(radio => radio.addEventListener('change', renderRecipients));
        function advance() {
            const current = state();
            if (!current.valid || current.total <= 0) return;
            if (!contactsOpen && !config.frozen) {
                contactsOpen = true; renderRecipients(); update();
                element.querySelector('#sales-contact-title').focus();
                contacts.scrollIntoView({ block: 'start', behavior: 'instant' });
            } else form.requestSubmit();
        }
        primary.forEach(button => button.addEventListener('click', advance));
        form.addEventListener('submit', event => {
            if (submitting) { event.preventDefault(); return; }
            const changingLanguage = event.submitter?.matches('[data-sales-language-submit]') === true;
            if (!changingLanguage && !contactsOpen && !config.frozen) { event.preventDefault(); advance(); return; }
            const current = state();
            if (!changingLanguage && (!current.valid || current.total <= 0 || (!config.frozen && !form.reportValidity()))) { event.preventDefault(); return; }
            // A language change posts only to the separate, non-payment presentation action.
            // Preserve incomplete recipients and quantities without browser storage or validation loss.
            const contactsOpenField = form.querySelector('[data-sales-contacts-open]');
            if (contactsOpenField) contactsOpenField.value = contactsOpen ? 'true' : 'false';
            // Post only the selected type IDs and counts, in contiguous binder indices.
            // Large offers need not submit hundreds of zero rows; browser prices are never posted.
            form.querySelector('[data-sales-selected-payload]')?.remove();
            const payload = document.createElement('div'); payload.hidden = true; payload.dataset.salesSelectedPayload = '';
            let selectedIndex = 0;
            config.types.forEach(type => {
                const control = controls.get(type.id);
                control.row.querySelectorAll('[name^="Tickets["]').forEach(input => input.removeAttribute('name'));
                const quantity = quantities.get(type.id) || 0;
                if (quantity <= 0) return;
                [['TicketTypeId', type.id], ['Quantity', quantity]].forEach(([name, value]) => {
                    const input = document.createElement('input'); input.type = 'hidden';
                    input.name = 'Tickets[' + selectedIndex + '].' + name; input.value = value; payload.append(input);
                });
                selectedIndex++;
            });
            form.append(payload);
            submitting = true; form.setAttribute('aria-busy', 'true'); update();
        });
        // Browser back/forward cache must not strand a previously submitted page in its busy state.
        window.addEventListener('pageshow', () => { submitting = false; form.removeAttribute('aria-busy'); update(); });
        const dialog = element.querySelector('[data-sales-image-dialog]');
        if (dialog) {
            const opener = element.querySelector('[data-sales-enlarge]');
            opener.addEventListener('click', () => dialog.showModal());
            element.querySelector('[data-sales-close-image]').addEventListener('click', () => dialog.close());
            dialog.addEventListener('close', () => opener.focus());
            dialog.addEventListener('click', event => { if (event.target === dialog) { const rect = dialog.getBoundingClientRect(); if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close(); } });
        }
        renderRecipients(); update();
        const error = element.querySelector('[data-sales-error]');
        if (error) error.focus();
    }
    return { admissionKey, admissions, normalizedAmount, readableForeground, localizedDate, translation, translateNodes, renderTimes, mount };
});
