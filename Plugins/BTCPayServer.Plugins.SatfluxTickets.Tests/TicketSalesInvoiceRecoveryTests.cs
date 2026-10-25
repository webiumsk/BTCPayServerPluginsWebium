using BTCPayServer.Client.Models;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Services.Invoices;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketSalesInvoiceRecoveryTests
{
    private static Order Order() => new() { Id = "order", StoreId = "store", TxnId = "server-nonce", Currency = "EUR", TotalAmount = 12.34m };
    private static InvoiceEntity Invoice(InvoiceStatus status = InvoiceStatus.New) => new()
    {
        Id = "invoice", StoreId = "store", Price = 12.34m, Currency = "EUR", Status = status,
        Metadata = new InvoiceMetadata { OrderId = "order", AdditionalData = new Dictionary<string, Newtonsoft.Json.Linq.JToken> { ["orderId"] = "order", ["TxnId"] = "server-nonce" } },
        InternalTags = new() { SimpleTicketSalesHostedService.TICKET_SALES_PREFIX + "server-nonce" }
    };

    private static Task<InvoiceEntity> Recover(Order order, Func<Task<InvoiceEntity[]>> find, Func<Task<InvoiceEntity>> create,
        Func<Task>? persist = null) => TicketSalesInvoiceRecovery.FindOrCreateAsync(order, find, persist ?? (() => Task.CompletedTask), create);

    [Theory]
    [InlineData(InvoiceStatus.New)]
    [InlineData(InvoiceStatus.Settled)]
    [InlineData(InvoiceStatus.Expired)]
    public async Task Existing_invoice_including_unpaid_and_settled_is_reused(InvoiceStatus status)
    {
        var invoice = Invoice(status);
        var creates = 0;
        var result = await Recover(Order(), () => Task.FromResult(new[] { invoice }),
            () => { creates++; return Task.FromResult(Invoice()); });
        Assert.Same(invoice, result); Assert.Equal(0, creates);
        Assert.Equal(new[] { "server-nonce" }, invoice.GetInternalTags(SimpleTicketSalesHostedService.TICKET_SALES_PREFIX));
        Assert.Equal(new[] { "" }, invoice.GetInternalTags(SimpleTicketSalesHostedService.TICKET_SALES_PREFIX + "server-nonce"));
    }

    [Theory]
    [InlineData("store")]
    [InlineData("order")]
    [InlineData("amount")]
    [InlineData("currency")]
    [InlineData("metadata")]
    [InlineData("nonce")]
    public async Task Conflicting_invoice_is_fail_closed_without_creating_another_invoice(string mismatch)
    {
        var invoice = Invoice();
        if (mismatch == "store") invoice.StoreId = "foreign";
        if (mismatch == "order") invoice.Metadata.OrderId = "another-order";
        if (mismatch == "amount") invoice.Price = .01m;
        if (mismatch == "currency") invoice.Currency = "USD";
        if (mismatch == "metadata") invoice.Metadata = null!;
        if (mismatch == "nonce") invoice.Metadata.AdditionalData["TxnId"] = "different-nonce";
        var creates = 0;
        Assert.False(TicketSalesInvoiceRecovery.Matches(invoice, Order()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Recover(Order(),
            () => Task.FromResult(new[] { invoice }), () => { creates++; return Task.FromResult(Invoice()); }));
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task Unavailable_invoice_lookup_never_falls_back_to_creation()
    {
        var creates = 0;
        await Assert.ThrowsAsync<IOException>(() => Recover(Order(),
            () => throw new IOException("Synthetic unavailable repository"),
            () => { creates++; return Task.FromResult(Invoice()); }));
        Assert.Equal(0, creates);
    }

    [Fact]
    public async Task Upstream_success_with_uncertain_response_is_recovered_on_retry_without_duplicate()
    {
        InvoiceEntity? persisted = null;
        var creates = 0;
        Task<InvoiceEntity[]> Find() => Task.FromResult(persisted == null ? Array.Empty<InvoiceEntity>() : new[] { persisted });
        Task<InvoiceEntity> Create()
        {
            creates++; persisted = Invoice();
            throw new IOException("Synthetic response lost after invoice persistence");
        }
        await Assert.ThrowsAsync<IOException>(() => Recover(Order(), Find, Create));
        var retried = await Recover(Order(), Find, Create);
        Assert.Same(persisted, retried); Assert.Equal(1, creates);
    }

    [Fact]
    public async Task Local_invoice_id_save_failure_does_not_require_creating_a_new_invoice()
    {
        var order = Order();
        var persisted = Invoice(InvoiceStatus.Settled);
        var creates = 0;
        var invoice = await Recover(order, () => Task.FromResult(new[] { persisted }),
            () => { creates++; return Task.FromResult(Invoice()); });
        // Simulate failure to store this returned ID; the immutable order intent remains intact.
        Assert.Null(order.InvoiceId);
        var retry = await Recover(order, () => Task.FromResult(new[] { persisted }),
            () => { creates++; return Task.FromResult(Invoice()); });
        Assert.Same(invoice, retry); Assert.Equal(0, creates);
        // Fulfillment still belongs to the independently reviewed hosted-service reconciliation path.
    }

    [Fact]
    public void Exact_nonce_matching_cannot_confuse_prefix_collisions()
    {
        var invoice = Invoice();
        invoice.InternalTags = new() { SimpleTicketSalesHostedService.TICKET_SALES_PREFIX + "server-nonce-extra" };
        Assert.False(TicketSalesInvoiceRecovery.Matches(invoice, Order()));
        Assert.Null(TicketSalesInvoiceRecovery.Select(new[] { invoice }, Order()));
    }
    [Fact]
    public async Task Uncertain_attempt_is_lookup_only_even_before_invoice_becomes_visible()
    {
        var order = Order();
        InvoiceEntity? visible = null;
        var creates = 0; var durableMarkers = 0;
        Task<InvoiceEntity[]> Find() => Task.FromResult(visible == null ? Array.Empty<InvoiceEntity>() : new[] { visible });
        Task<InvoiceEntity> Create() { creates++; throw new IOException("Synthetic uncertain host commit"); }
        Task Persist() { durableMarkers++; return Task.CompletedTask; }
        await Assert.ThrowsAsync<IOException>(() => Recover(order, Find, Create, Persist));
        Assert.Equal(TicketSalesInvoiceRecovery.CreationAttempted, order.InvoiceStatus);
        await Assert.ThrowsAsync<TicketSalesInvoicePendingException>(() => Recover(order, Find, Create, Persist));
        Assert.Equal(1, creates); Assert.Equal(1, durableMarkers);
        visible = Invoice(InvoiceStatus.Settled);
        Assert.Same(visible, await Recover(order, Find, Create, Persist));
        Assert.Equal(1, creates);
    }

    [Fact]
    public async Task Failed_durable_attempt_marker_prevents_external_creation()
    {
        var creates = 0;
        await Assert.ThrowsAsync<IOException>(() => Recover(Order(), () => Task.FromResult(Array.Empty<InvoiceEntity>()),
            () => { creates++; return Task.FromResult(Invoice()); }, () => throw new IOException("Synthetic marker save failure")));
        Assert.Equal(0, creates);
    }
}

