using System;
using System.Collections.Generic;
using System.Globalization;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

/// <summary>New merchant-order copy, independent of the host's existing translation catalog.</summary>
public static class TicketTypeOrderText
{
    private static readonly string[] Keys = ["Edit", "Save", "Cancel", "Reload", "PublishedHint", "SortedHint", "ShowPublished", "Position", "Drag", "MoveUp", "MoveDown", "Unsaved", "EditHint", "Moved", "Saving", "Conflict", "SaveFailed", "Uncertain", "Invalid", "Saved"];
    private static readonly IReadOnlyDictionary<string, string[]> Text = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["en"] = ["Edit published order", "Save order", "Cancel", "Reload current order", "This order is used on the public sales page.", "Sorting this table does not change the public sales page.", "Show published order", "Position", "Drag to reorder", "Move {0} up", "Move {0} down", "Unsaved order changes.", "Drag rows or use the move buttons.", "Moved to position {0}. Unsaved order changes.", "Saving order...", "Ticket types changed in another session. Reload the current order before editing again.", "The order could not be saved. Reload before trying again.", "The save result could not be confirmed. Reload before saving again.", "Invalid ticket type order.", "Ticket type order saved successfully"],
        ["sk"] = ["Upraviť poradie v ponuke", "Uložiť poradie", "Zrušiť", "Načítať aktuálne poradie", "Toto poradie sa používa vo verejnej ponuke lístkov.", "Zoradenie tejto tabuľky nemení verejnú ponuku lístkov.", "Zobraziť poradie v ponuke", "Poradie", "Presunutím zmeniť poradie", "Presunúť {0} vyššie", "Presunúť {0} nižšie", "Neuložené zmeny poradia.", "Presuňte riadky alebo použite tlačidlá na presun.", "Presunuté na pozíciu {0}. Neuložené zmeny poradia.", "Ukladá sa poradie...", "Typy lístkov sa zmenili v inom okne. Pred ďalšou úpravou načítajte aktuálne poradie.", "Poradie sa nepodarilo uložiť. Pred ďalším pokusom obnovte stránku.", "Výsledok uloženia sa nepodarilo potvrdiť. Pred ďalším uložením obnovte stránku.", "Neplatné poradie typov lístkov.", "Poradie typov lístkov bolo uložené"],
        ["es"] = ["Editar orden de venta", "Guardar orden", "Cancelar", "Recargar orden actual", "Este orden se usa en la página pública de venta.", "Ordenar esta tabla no cambia la página pública de venta.", "Mostrar orden de venta", "Posición", "Arrastrar para reordenar", "Subir {0}", "Bajar {0}", "Cambios de orden sin guardar.", "Arrastra las filas o usa los botones para moverlas.", "Movido a la posición {0}. Cambios de orden sin guardar.", "Guardando orden...", "Los tipos de entradas cambiaron en otra sesión. Recarga el orden actual antes de editarlo.", "No se pudo guardar el orden. Recarga antes de intentarlo de nuevo.", "No se pudo confirmar el resultado. Recarga antes de volver a guardar.", "Orden de tipos de entradas no válido.", "Orden de tipos de entradas guardado"],
        ["de"] = ["Verkaufsreihenfolge bearbeiten", "Reihenfolge speichern", "Abbrechen", "Aktuelle Reihenfolge laden", "Diese Reihenfolge wird auf der öffentlichen Verkaufsseite verwendet.", "Das Sortieren dieser Tabelle ändert die öffentliche Verkaufsseite nicht.", "Verkaufsreihenfolge anzeigen", "Position", "Zum Sortieren ziehen", "{0} nach oben verschieben", "{0} nach unten verschieben", "Nicht gespeicherte Änderungen der Reihenfolge.", "Zeilen ziehen oder die Schaltflächen zum Verschieben verwenden.", "An Position {0} verschoben. Nicht gespeicherte Änderungen der Reihenfolge.", "Reihenfolge wird gespeichert...", "Die Tickettypen wurden in einer anderen Sitzung geändert. Vor der Bearbeitung die aktuelle Reihenfolge laden.", "Die Reihenfolge konnte nicht gespeichert werden. Vor dem nächsten Versuch neu laden.", "Das Ergebnis konnte nicht bestätigt werden. Vor dem erneuten Speichern neu laden.", "Ungültige Reihenfolge der Tickettypen.", "Reihenfolge der Tickettypen gespeichert"],
        ["cs"] = ["Upravit pořadí v nabídce", "Uložit pořadí", "Zrušit", "Načíst aktuální pořadí", "Toto pořadí se používá ve veřejné nabídce vstupenek.", "Seřazení této tabulky nemění veřejnou nabídku vstupenek.", "Zobrazit pořadí v nabídce", "Pořadí", "Přetažením změnit pořadí", "Přesunout {0} výše", "Přesunout {0} níže", "Neuložené změny pořadí.", "Přesuňte řádky nebo použijte tlačítka pro přesun.", "Přesunuto na pozici {0}. Neuložené změny pořadí.", "Ukládá se pořadí...", "Typy vstupenek se změnily v jiném okně. Před další úpravou načtěte aktuální pořadí.", "Pořadí se nepodařilo uložit. Před dalším pokusem obnovte stránku.", "Výsledek uložení se nepodařilo potvrdit. Před dalším uložením obnovte stránku.", "Neplatné pořadí typů vstupenek.", "Pořadí typů vstupenek bylo uloženo"]
    };

    public static IReadOnlyDictionary<string, string> GetAll(string culture = null)
    {
        var language = (culture ?? CultureInfo.CurrentUICulture.Name).Split('-')[0].ToLowerInvariant();
        if (!Text.TryGetValue(language, out var values)) values = Text["en"];
        if (values.Length != Keys.Length) throw new InvalidOperationException("Incomplete ticket type order translations.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < Keys.Length; index++) result.Add(Keys[index], values[index]);
        return result;
    }

    public static string Get(string key) => GetAll()[key];
    public static string Format(string key, object value) => string.Format(CultureInfo.CurrentUICulture, Get(key), value);
}
