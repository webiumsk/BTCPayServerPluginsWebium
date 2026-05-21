#nullable enable
using System;
using System.Globalization;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public static class RafflePricing
{
    public const string SatsCurrency = "SATS";

    public static string NormalizeCurrency(string currency) =>
        currency.Trim().ToUpperInvariant();

    public static void ApplyPricing(Raffle raffle, string currency, decimal price)
    {
        currency = NormalizeCurrency(currency);
        if (price <= 0)
            throw new ArgumentException("Ticket price must be positive");

        if (currency == SatsCurrency)
        {
            if (price != decimal.Truncate(price))
                throw new ArgumentException("SATS price must be a whole number");
            raffle.TicketCurrency = SatsCurrency;
            raffle.TicketPrice = price;
            raffle.TicketPriceSats = (long)price;
            return;
        }

        raffle.TicketCurrency = currency;
        raffle.TicketPrice = price;
        raffle.TicketPriceSats = 0;
    }

    public static long? DisplayTicketPriceSats(Raffle raffle) =>
        NormalizeCurrency(raffle.TicketCurrency) == SatsCurrency
            ? raffle.TicketPriceSats
            : null;

    public static string FormatTicketPrice(Raffle raffle) =>
        FormatAmount(raffle.TicketPrice, raffle.TicketCurrency);

    public static int AmountDecimalPlaces(string currency) =>
        NormalizeCurrency(currency) switch
        {
            SatsCurrency => 0,
            "BTC" => 8,
            _ => 2
        };

    public static string FormatAmount(decimal amount, string currency)
    {
        var norm = NormalizeCurrency(currency);
        if (norm == SatsCurrency)
            return $"{decimal.Truncate(amount).ToString("N0", CultureInfo.InvariantCulture)} sats";

        var places = AmountDecimalPlaces(norm);
        var formatted = amount.ToString($"N{places}", CultureInfo.InvariantCulture);
        return $"{formatted} {norm}";
    }
}
