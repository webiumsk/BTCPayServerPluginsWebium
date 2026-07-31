using System.Text.Json;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

public class NopBackoffTests
{
    [Fact]
    public void Follows_the_manual_schedule()
    {
        // Manual: exponential 1 s, 2 s, 4 s ... max 30 s, max 5 attempts.
        Assert.Equal(TimeSpan.FromSeconds(1), NopBackoff.DelayForAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(2), NopBackoff.DelayForAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(4), NopBackoff.DelayForAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(8), NopBackoff.DelayForAttempt(4));
        Assert.Equal(TimeSpan.FromSeconds(16), NopBackoff.DelayForAttempt(5));
        Assert.Equal(TimeSpan.FromSeconds(30), NopBackoff.DelayForAttempt(6));
        Assert.Equal(TimeSpan.FromSeconds(30), NopBackoff.DelayForAttempt(99));
        Assert.Equal(5, NopBackoff.MaxAttempts);
        Assert.Equal(4, NopBackoff.RetryDelays().Count());
    }

    [Fact]
    public void Transaction_id_response_accepts_both_field_names()
    {
        // Services API doc says "id"; the integration manual example shows
        // "transaction_id" - both must parse.
        var withId = JsonSerializer.Deserialize<JsonElement>(
            "{\"id\": \"QR-88311a892b394a4db1af284e5c754bbb\", \"created_at\": \"2025-07-13T21:33:09.231Z\"}");
        var withTransactionId = JsonSerializer.Deserialize<JsonElement>(
            "{\"transaction_id\": \"QR-01c40ef8bb2541659c2bd4abfb6a9964\", \"created_at\": \"2025-08-16T20:17:30.345Z\"}");

        Assert.Equal("QR-88311a892b394a4db1af284e5c754bbb", NopRestClient.ReadTransactionId(withId));
        Assert.Equal("QR-01c40ef8bb2541659c2bd4abfb6a9964", NopRestClient.ReadTransactionId(withTransactionId));
        Assert.Null(NopRestClient.ReadTransactionId(JsonSerializer.Deserialize<JsonElement>("{}")));
    }
}
