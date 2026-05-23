using System.Globalization;
using BTCPayServer.Plugins.BTCPayRaffle.Services;
using Xunit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

public class RaffleStringLocalizerTests
{
    [Fact]
    public void Get_uses_explicit_culture()
    {
        var loc = new RaffleStringLocalizer(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        Assert.Equal("Ready to draw", loc.Get("draw.ready", new CultureInfo("en")));
        Assert.Equal("Posledné vylosované číslo", loc.Get("wallet.last_drawn_label", new CultureInfo("sk")));
    }

    [Fact]
    public void Format_substitutes_placeholders()
    {
        var loc = new RaffleStringLocalizer(new Microsoft.AspNetCore.Http.HttpContextAccessor());
        Assert.Equal("Draw #3", loc.Format("draw.draw_number", 3));
    }

    [Fact]
    public void PickLanguageFromAcceptHeader_prefers_higher_q()
    {
        Assert.Equal("sk", RaffleStringLocalizer.PickLanguageFromAcceptHeader("en;q=0.8,sk-SK;q=0.9"));
        Assert.Equal("es", RaffleStringLocalizer.PickLanguageFromAcceptHeader("es-ES,en;q=0.5"));
    }

    [Fact]
    public void NormalizeLanguageCode_supports_en_sk_es_only()
    {
        Assert.Equal("sk", RaffleStringLocalizer.NormalizeLanguageCode("sk-SK"));
        Assert.Null(RaffleStringLocalizer.NormalizeLanguageCode("de"));
    }
}
