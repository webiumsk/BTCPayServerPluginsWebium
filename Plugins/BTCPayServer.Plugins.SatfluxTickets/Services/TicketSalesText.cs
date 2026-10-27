using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using BTCPayServer.Rating;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>Public sales-only copy; host translations remain the authority outside this page.</summary>
public sealed class TicketSalesText
{
    private static readonly string[] Languages = { "en", "sk", "es", "de", "cs" };
    private static readonly IReadOnlyDictionary<string, string[]> Copy = new Dictionary<string, string[]>
    {
        ["NativeScheduleChanged"] = new[] { "The event schedule changed while you were editing. Reload the page before trying again. Your draft remains visible here.", "Čas podujatia sa počas úprav zmenil. Pred ďalším pokusom obnov stránku. Tvoj návrh zostáva zobrazený tu.", "El horario del evento cambió mientras editabas. Recarga la página antes de intentarlo de nuevo. Tu borrador sigue visible aquí.", "Der Veranstaltungszeitplan wurde während der Bearbeitung geändert. Vor dem erneuten Versuch die Seite neu laden. Der Entwurf bleibt hier sichtbar.", "Čas události se během úprav změnil. Před dalším pokusem obnov stránku. Tvůj návrh zůstává zobrazený zde." },
        ["VenueTimezone"] = new[] { "Venue time zone", "Časové pásmo miesta konania", "Zona horaria del lugar", "Zeitzone des Veranstaltungsorts", "Časové pásmo místa konání" },
        ["MissingVenueTimezone"] = new[] { "The venue time zone is missing or unsupported. Times are shown in UTC.", "Časové pásmo miesta konania chýba alebo nie je podporované. Časy sú v UTC.", "La zona horaria del lugar falta o no es compatible. Las horas se muestran en UTC.", "Die Zeitzone des Veranstaltungsorts fehlt oder wird nicht unterstützt. Zeiten werden in UTC angezeigt.", "Časové pásmo místa konání chybí nebo není podporované. Časy jsou v UTC." },
        ["TimeZoneRequired"] = new[] { "Select a time zone for local date and time.", "Vyber časové pásmo pre miestny dátum a čas.", "Elige una zona horaria para la fecha y hora local.", "Eine Zeitzone für lokale Datums- und Zeitangaben auswählen.", "Vyber časové pásmo pro místní datum a čas." },
        ["TimeZoneInvalid"] = new[] { "Select a supported time zone.", "Vyber podporované časové pásmo.", "Elige una zona horaria compatible.", "Eine unterstützte Zeitzone auswählen.", "Vyber podporované časové pásmo." },
        ["CivilInvalid"] = new[] { "Enter a valid local date and time without a UTC offset.", "Zadaj platný miestny dátum a čas bez posunu UTC.", "Introduce una fecha y hora local válida sin desplazamiento UTC.", "Ein gültiges lokales Datum mit Uhrzeit ohne UTC-Versatz eingeben.", "Zadej platné místní datum a čas bez posunu UTC." },
        ["CivilMissing"] = new[] { "This local time does not exist because the clocks change. Choose another time.", "Tento miestny čas neexistuje pre zmenu času. Vyber iný čas.", "Esta hora local no existe por el cambio de hora. Elige otra hora.", "Diese lokale Uhrzeit existiert wegen der Zeitumstellung nicht. Eine andere Uhrzeit wählen.", "Tento místní čas neexistuje kvůli změně času. Vyber jiný čas." },
        ["CivilAmbiguous"] = new[] { "This local time occurs twice because the clocks change. Choose an unambiguous time.", "Tento miestny čas sa pri zmene času vyskytuje dvakrát. Vyber jednoznačný čas.", "Esta hora local se repite por el cambio de hora. Elige una hora inequívoca.", "Diese lokale Uhrzeit tritt wegen der Zeitumstellung zweimal auf. Eine eindeutige Uhrzeit wählen.", "Tento místní čas se při změně času vyskytuje dvakrát. Vyber jednoznačný čas." },
        ["InstantOffsetRequired"] = new[] { "Instant dates must include Z or an explicit UTC offset.", "Dátum okamihu musí obsahovať Z alebo explicitný posun UTC.", "La fecha debe incluir Z o un desplazamiento UTC explícito.", "Ein Zeitpunkt muss Z oder einen expliziten UTC-Versatz enthalten.", "Datum okamžiku musí obsahovat Z nebo explicitní posun UTC." },
        ["TimeConflict"] = new[] { "Send either a local date or a UTC instant, not both.", "Pošli buď miestny dátum, alebo okamih UTC, nie oba naraz.", "Envía una fecha local o un instante UTC, no ambos.", "Entweder ein lokales Datum oder einen UTC-Zeitpunkt senden, nicht beides.", "Pošli buď místní datum, nebo okamžik UTC, ne obojí." },
        ["StartRequired"] = new[] { "Event start date is required.", "Začiatok podujatia je povinný.", "La fecha de inicio es obligatoria.", "Der Veranstaltungsbeginn ist erforderlich.", "Začátek události je povinný." },
        ["EndBefore"] = new[] { "Event end cannot be before its start.", "Koniec podujatia nemôže byť pred začiatkom.", "El final no puede ser anterior al inicio.", "Das Ende darf nicht vor dem Beginn liegen.", "Konec události nemůže být před začátkem." },
        ["EditorTimezone"] = new[] { "Date and time entry zone", "Časové pásmo zadávaného dátumu a času", "Zona de entrada de fecha y hora", "Zeitzone der Datums- und Zeiteingabe", "Časové pásmo zadávaného data a času" },
        ["ChooseTimezone"] = new[] { "Choose a time zone", "Vyber časové pásmo", "Elige una zona horaria", "Zeitzone auswählen", "Vyber časové pásmo" },
        ["StartupError"] = new[] { "Ticket selection could not be loaded. Reload this page; if the problem continues, contact the organiser.", "Výber lístkov sa nepodarilo načítať. Obnov stránku; ak problém pretrváva, kontaktuj organizátora.", "No se pudo cargar la selección de entradas. Recarga la página; si el problema continúa, contacta con el organizador.", "Die Ticketauswahl konnte nicht geladen werden. Seite neu laden; falls das Problem bleibt, den Veranstalter kontaktieren.", "Výběr vstupenek se nepodařilo načíst. Obnov stránku; pokud problém přetrvává, kontaktuj pořadatele." },
        ["Language"] = new[] { "Language", "Jazyk", "Idioma", "Sprache", "Jazyk" },
        ["ApplyLanguage"] = new[] { "Change language", "Zmeniť jazyk", "Cambiar idioma", "Sprache ändern", "Změnit jazyk" },
        ["YourTimezone"] = new[] { "Your time zone", "Tvoje časové pásmo", "Tu zona horaria", "Deine Zeitzone", "Tvoje časové pásmo" },
        ["ChooseTickets"] = new[] { "Choose your tickets", "Vyber si lístky", "Elige tus entradas", "Tickets auswählen", "Vyber si vstupenky" },
        ["Summary"] = new[] { "Order summary", "Zhrnutie objednávky", "Resumen del pedido", "Bestellübersicht", "Shrnutí objednávky" },
        ["About"] = new[] { "About this event", "O podujatí", "Sobre el evento", "Über die Veranstaltung", "O události" },
        ["Physical"] = new[] { "In-person event", "Fyzické podujatie", "Evento presencial", "Präsenzveranstaltung", "Fyzická událost" },
        ["Virtual"] = new[] { "Virtual event", "Virtuálne podujatie", "Evento virtual", "Online-Veranstaltung", "Virtuální událost" },
        ["Location"] = new[] { "Location", "Miesto", "Ubicación", "Veranstaltungsort", "Místo" },
        ["Starts"] = new[] { "Starts", "Začiatok", "Inicio", "Beginn", "Začátek" },
        ["Ends"] = new[] { "Ends", "Koniec", "Fin", "Ende", "Konec" },
        ["DeliveryNote"] = new[] { "Tickets with QR codes will be emailed after payment.", "Lístky s QR kódmi dostaneš e-mailom po zaplatení.", "Recibirás las entradas con códigos QR por correo tras el pago.", "Tickets mit QR-Codes werden nach der Zahlung per E-Mail versendet.", "Vstupenky s QR kódy dostaneš e-mailem po zaplacení." },
        ["VirtualNote"] = new[] { "Online event. Follow the organiser's instructions in the confirmation email.", "Online podujatie. Riaď sa pokynmi organizátora v potvrdzovacom e-maile.", "Evento en línea. Sigue las instrucciones del organizador en el correo de confirmación.", "Online-Veranstaltung. Bitte die Hinweise des Veranstalters in der Bestätigungs-E-Mail beachten.", "Online událost. Řiď se pokyny pořadatele v potvrzovacím e-mailu." },
        ["Quantity"] = new[] { "Quantity", "Počet", "Cantidad", "Anzahl", "Počet" },
        ["Decrease"] = new[] { "Decrease quantity", "Znížiť počet", "Reducir cantidad", "Anzahl verringern", "Snížit počet" },
        ["Increase"] = new[] { "Increase quantity", "Zvýšiť počet", "Aumentar cantidad", "Anzahl erhöhen", "Zvýšit počet" },
        ["SoldOut"] = new[] { "Sold out", "Vypredané", "Agotadas", "Ausverkauft", "Vyprodáno" },
        ["NoTickets"] = new[] { "No tickets selected", "Zatiaľ nemáš vybrané lístky", "No has elegido entradas", "Keine Tickets ausgewählt", "Zatím nemáš vybrané vstupenky" },
        ["NoOffer"] = new[] { "There are no ticket types available at the moment.", "Momentálne nie sú dostupné žiadne typy lístkov.", "No hay tipos de entrada disponibles en este momento.", "Zurzeit sind keine Ticketarten verfügbar.", "Momentálně nejsou dostupné žádné typy vstupenek." },
        ["Total"] = new[] { "Total", "Spolu", "Total", "Gesamt", "Celkem" },
        ["Continue"] = new[] { "Continue to contact details", "Pokračovať na kontakty", "Continuar a los datos de contacto", "Weiter zu den Kontaktdaten", "Pokračovat na kontakty" },
        ["Pay"] = new[] { "Continue to payment", "Pokračovať k platbe", "Continuar al pago", "Weiter zur Zahlung", "Pokračovat k platbě" },
        ["Contacts"] = new[] { "Ticket recipients", "Príjemcovia lístkov", "Destinatarios de las entradas", "Ticketempfänger", "Příjemci vstupenek" },
        ["Together"] = new[] { "Send all tickets to one email", "Poslať všetky lístky na jeden e-mail", "Enviar todas las entradas a un correo", "Alle Tickets an eine E-Mail-Adresse senden", "Poslat všechny vstupenky na jeden e-mail" },
        ["Individual"] = new[] { "Send each ticket separately", "Poslať každý lístok samostatne", "Enviar cada entrada por separado", "Jedes Ticket einzeln senden", "Poslat každou vstupenku samostatně" },
        ["Email"] = new[] { "Email address", "E-mailová adresa", "Correo electrónico", "E-Mail-Adresse", "E-mailová adresa" },
        ["FirstName"] = new[] { "First name", "Meno", "Nombre", "Vorname", "Jméno" },
        ["LastName"] = new[] { "Last name", "Priezvisko", "Apellidos", "Nachname", "Příjmení" },
        ["Ticket"] = new[] { "Ticket", "Lístok", "Entrada", "Ticket", "Vstupenka" },
        ["PaymentNote"] = new[] { "Payment takes place securely in BTCPay. Your tickets are issued after payment is confirmed.", "Platba prebehne v BTCPay. Lístky sa vystavia po potvrdení platby.", "El pago se realiza en BTCPay. Las entradas se emiten al confirmarse el pago.", "Die Zahlung erfolgt in BTCPay. Tickets werden nach Zahlungsbestätigung ausgestellt.", "Platba proběhne v BTCPay. Vstupenky se vystaví po potvrzení platby." },
        ["AvailabilityNote"] = new[] { "Availability is checked again before payment. Selecting tickets does not reserve them.", "Dostupnosť overíme znovu pred platbou. Výber lístkov nie je rezervácia.", "Se comprueba la disponibilidad antes del pago. Elegir entradas no las reserva.", "Die Verfügbarkeit wird vor der Zahlung erneut geprüft. Eine Auswahl reserviert keine Tickets.", "Dostupnost ověříme znovu před platbou. Výběr vstupenek není rezervace." },
        ["SelectionChanged"] = new[] { "Your selection is no longer available or exceeds the purchase limit. Review the quantities and try again.", "Výber už nie je dostupný alebo prekračuje limit nákupu. Skontroluj počty a skús znova.", "La selección ya no está disponible o supera el límite. Revisa las cantidades e inténtalo de nuevo.", "Die Auswahl ist nicht mehr verfügbar oder überschreitet das Kauflimit. Bitte die Anzahl prüfen.", "Výběr již není dostupný nebo překračuje limit nákupu. Zkontroluj počty a zkus to znovu." },
        ["ChooseFirst"] = new[] { "Choose at least one ticket to continue.", "Vyber aspoň jeden lístok.", "Elige al menos una entrada para continuar.", "Bitte mindestens ein Ticket auswählen.", "Vyber alespoň jednu vstupenku." },
        ["ContactsInvalid"] = new[] { "Enter a first name, last name and valid delivery email for every ticket.", "Pri každom lístku vyplň meno, priezvisko a platný doručovací e-mail.", "Indica nombre, apellidos y un correo válido para cada entrada.", "Für jedes Ticket Vorname, Nachname und eine gültige E-Mail-Adresse angeben.", "U každé vstupenky vyplň jméno, příjmení a platný doručovací e-mail." },
        ["ZeroPrice"] = new[] { "This checkout does not support orders with a zero total. Contact the organiser for these tickets.", "Tento predaj nepodporuje objednávky s nulovou sumou. Pre tieto lístky kontaktuj organizátora.", "Este proceso no admite pedidos de importe cero. Contacta con el organizador.", "Diese Kasse unterstützt keine Bestellungen mit Gesamtbetrag null. Bitte den Veranstalter kontaktieren.", "Tento prodej nepodporuje objednávky s nulovou částkou. Pro tyto vstupenky kontaktuj pořadatele." },
        ["UpstreamError"] = new[] { "Payment could not be opened. Your details are preserved. Try again; an existing checkout will be reused.", "Platbu sa nepodarilo otvoriť. Údaje zostali zachované. Skús znova; existujúcu platbu použijeme opätovne.", "No se pudo abrir el pago. Tus datos se conservan. Reinténtalo; se reutilizará un pago existente.", "Die Zahlung konnte nicht geöffnet werden. Die Daten bleiben erhalten. Erneut versuchen; eine bestehende Zahlung wird wiederverwendet.", "Platbu se nepodařilo otevřít. Údaje zůstaly zachované. Zkus to znovu; existující platbu použijeme opětovně." },
        ["CheckoutStarted"] = new[] { "A payment was already started for this selection. Continue to the existing checkout.", "Pre tento výber už bola vytvorená platba. Pokračuj na existujúcu platbu.", "Ya se inició un pago para esta selección. Continúa al pago existente.", "Für diese Auswahl wurde bereits eine Zahlung gestartet. Zur bestehenden Zahlung wechseln.", "Pro tento výběr již byla vytvořena platba. Pokračuj na existující platbu." },
        ["VerifyPayment"] = new[] { "Check payment status", "Overiť stav platby", "Comprobar el estado del pago", "Zahlungsstatus prüfen", "Ověřit stav platby" },
        ["PendingVerification"] = new[] { "Your payment may already have been created. Check its status again. If it remains unavailable, contact the organiser; another payment will not be created for this order.", "Platba už mohla byť vytvorená. Over jej stav znovu. Ak zostane nedostupná, kontaktuj organizátora; pre túto objednávku nevytvoríme ďalšiu platbu.", "Es posible que el pago ya exista. Comprueba su estado. Si sigue sin aparecer, contacta con el organizador; no se creará otro pago para este pedido.", "Die Zahlung wurde möglicherweise bereits erstellt. Status erneut prüfen. Falls sie nicht verfügbar bleibt, den Veranstalter kontaktieren; für diese Bestellung wird keine weitere Zahlung erstellt.", "Platba již mohla být vytvořena. Ověř její stav znovu. Pokud zůstane nedostupná, kontaktuj pořadatele; pro tuto objednávku nevytvoříme další platbu." },
        ["SessionExpired"] = new[] { "Your checkout session expired. Review the selection and contact details before continuing.", "Relácia objednávky vypršala. Pred pokračovaním skontroluj výber a kontakty.", "La sesión ha caducado. Revisa la selección y los datos antes de continuar.", "Die Bestellsitzung ist abgelaufen. Auswahl und Kontaktdaten vor dem Fortfahren prüfen.", "Relace objednávky vypršela. Před pokračováním zkontroluj výběr a kontakty." },
        ["Enlarge"] = new[] { "Enlarge event image", "Zväčšiť obrázok podujatia", "Ampliar imagen del evento", "Veranstaltungsbild vergrößern", "Zvětšit obrázek události" },
        ["Close"] = new[] { "Close", "Zavrieť", "Cerrar", "Schließen", "Zavřít" },
        ["Working"] = new[] { "Opening payment…", "Otváram platbu…", "Abriendo el pago…", "Zahlung wird geöffnet…", "Otevírám platbu…" },
        ["TicketCount"] = new[] { "Selected tickets", "Vybrané lístky", "Entradas seleccionadas", "Ausgewählte Tickets", "Vybrané vstupenky" },
        ["Limit"] = new[] { "Up to 10 per type and 100 tickets per order.", "Najviac 10 z každého typu a 100 lístkov na objednávku.", "Hasta 10 por tipo y 100 entradas por pedido.", "Bis zu 10 je Ticketart und 100 Tickets pro Bestellung.", "Nejvýše 10 z každého typu a 100 vstupenek na objednávku." },
        ["Javascript"] = new[] { "Enable JavaScript to select tickets and enter recipient details.", "Na výber lístkov a vyplnenie príjemcov povoľ JavaScript.", "Activa JavaScript para elegir entradas e introducir destinatarios.", "JavaScript zum Auswählen von Tickets und Eingeben der Empfänger aktivieren.", "Pro výběr vstupenek a vyplnění příjemců povol JavaScript." },
    };

    public string Language { get; }
    public CultureInfo Culture { get; }
    private readonly int _languageIndex;
    // The host deliberately uses invariant culture; anonymous buyers need an independent,
    // bounded negotiation path. Never change thread culture, cookies or payment settings.
    public static string ResolveLanguage(string explicitLanguage, string acceptLanguage)
    {
        var requested = SupportedLanguage(explicitLanguage);
        if (requested != null) return requested;
        if (string.IsNullOrEmpty(acceptLanguage) || acceptLanguage.Length > 4096) return "en";
        var best = "en";
        decimal bestQuality = 0;
        foreach (var item in acceptLanguage.Split(',').Take(32))
        {
            var parts = item.Split(';');
            var language = SupportedLanguage(parts[0].Trim());
            if (language == null || parts.Length > 2) continue;
            decimal quality = 1;
            if (parts.Length == 2)
            {
                var parameter = parts[1].Trim();
                if (!parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                    || !decimal.TryParse(parameter.AsSpan(2), NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out quality) || quality < 0 || quality > 1) continue;
            }
            if (quality <= bestQuality) continue;
            best = language;
            bestQuality = quality;
        }
        return best;
    }
    private static string SupportedLanguage(string language)
    {
        if (string.IsNullOrEmpty(language) || language.Length > 35) return null;
        // Accept regional tags, not lists, paths, HTML, arbitrary culture names or cookie data.
        var parts = language.Split('-', '_');
        if (parts.Any(p => p.Length == 0 || p.Length > 8
            || p.Any(c => !(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9')))) return null;
        var shortName = parts[0].ToLowerInvariant();
        return Array.IndexOf(Languages, shortName) >= 0 ? shortName : null;
    }
    public TicketSalesText(string language)
    {
        Language = SupportedLanguage(language) ?? "en";
        _languageIndex = Array.IndexOf(Languages, Language);
        Culture = CultureInfo.GetCultureInfo(Language);
    }
    // Hash only fixed, embedded public assets; request parameters never select a resource path.
    public static string ScriptVersion { get; } = AssetHash("Resources.js.ticket-sales.js");
    public static string StyleVersion { get; } = AssetHash("Resources.css.ticket-sales.css");
    private static string AssetHash(string resource)
    {
        using var stream = typeof(TicketSalesText).Assembly.GetManifestResourceStream(
            "BTCPayServer.Plugins.SatfluxTickets." + resource)
            ?? throw new InvalidOperationException("A public sales asset is missing.");
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Translations() =>
        Languages.ToDictionary(language => language, language => new TicketSalesText(language).ClientCopy());
    public string this[string key] => Copy.TryGetValue(key, out var translations) ? translations[_languageIndex] : key;
    public IReadOnlyDictionary<string, string> ClientCopy()
    {
        var result = new Dictionary<string, string>();
        foreach (var key in Copy.Keys) result[key] = this[key];
        return result;
    }
    public string Date(DateTime date) => DateTime.SpecifyKind(date, DateTimeKind.Utc).ToString("f", Culture) + " UTC";
    public string Money(decimal amount, int decimals, string currency)
    {
        var precision = Math.Clamp(decimals, 0, 28);
        var normalized = amount.RoundToSignificant(ref precision);
        return normalized.ToString("N" + precision, Culture) + " " + currency;
    }
}
