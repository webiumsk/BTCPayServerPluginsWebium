#nullable enable
using System;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketSalesSocialMetadataTests
{
    [Theory]
    [InlineData("/images/event.png", "https://tickets.example/images/event.png")]
    [InlineData("/btcpay/images/event.png", "https://tickets.example/btcpay/images/event.png")]
    [InlineData("images/event.png", "https://tickets.example/btcpay/images/event.png")]
    [InlineData("https://cdn.example/poster.png?v=2&size=large", "https://cdn.example/poster.png?v=2&size=large")]
    [InlineData("http://cdn.example/poster.png", "http://cdn.example/poster.png")]
    [InlineData("//cdn.example/poster.png", "https://cdn.example/poster.png")]
    [InlineData("images/event poster.png", "https://tickets.example/btcpay/images/event%20poster.png")]
    public void Existing_public_urls_are_absolute_without_losing_or_duplicating_PathBase(string value, string expected) =>
        Assert.Equal(expected, TicketSalesSocialMetadata.AbsoluteHttpUrl(new Uri("https://tickets.example/btcpay"), value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:image/png;base64,synthetic")]
    [InlineData("file:///tmp/private.png")]
    [InlineData("ftp://cdn.example/poster.png")]
    [InlineData("https://user:password@cdn.example/poster.png")]
    [InlineData("//user:password@cdn.example/poster.png")]
    [InlineData("https://cdn.example/poster.png\r\nX-Test: synthetic")]
    public void Missing_or_unsafe_urls_are_omitted(string? value) =>
        Assert.Null(TicketSalesSocialMetadata.AbsoluteHttpUrl(new Uri("https://tickets.example/btcpay"), value));

    [Fact]
    public void Event_image_has_precedence_and_logo_is_only_used_when_event_image_is_absent()
    {
        var root = new Uri("https://tickets.example/btcpay");
        Assert.Equal("https://tickets.example/poster.png", TicketSalesSocialMetadata.ImageUrl(root, "/poster.png", "/logo.png"));
        Assert.Equal("https://tickets.example/logo.png", TicketSalesSocialMetadata.ImageUrl(root, null, "/logo.png"));
        Assert.Null(TicketSalesSocialMetadata.ImageUrl(root, null, null));
        Assert.Null(TicketSalesSocialMetadata.ImageUrl(root, "javascript:synthetic", "/logo.png"));
        Assert.Null(TicketSalesSocialMetadata.ImageUrl(root, null, "https://user:password@cdn.example/logo.png"));
    }
}
