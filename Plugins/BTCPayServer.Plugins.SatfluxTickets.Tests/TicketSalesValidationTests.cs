using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using BTCPayServer.Plugins.SatfluxTickets.ViewModels;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketSalesValidationTests
{
    private static Event Event(bool capped = false) => new() { Id = "event", HasMaximumCapacity = capped, MaximumEventCapacity = capped ? 6 : null };
    private static TicketType Type(string id = "type", int quantity = 100, decimal price = 12.34m) =>
        new() { Id = id, EventId = "event", Name = "Saved name", Quantity = quantity, Price = price, TicketTypeState = EntityState.Active };
    private static TicketSelectionViewModel Selection(string id = "type", int quantity = 1) =>
        new() { TicketTypeId = id, Quantity = quantity, Price = .01m, TicketTypeName = "Forged name" };
    private static TicketSalesContactViewModel Contact(string id = "type", int index = 0, string email = "person@example.test") =>
        new() { TicketTypeId = id, AdmissionIndex = index, FirstName = " First ", LastName = " Last ", Email = email };

    [Fact]
    public void Prices_and_names_are_always_loaded_from_persisted_types()
    {
        var error = TicketSalesValidation.ValidateSelection(Event(), new[] { Type() }, new() { Selection(quantity: 2) }, 0, out var result);
        Assert.Null(error);
        var item = Assert.Single(result);
        Assert.Equal(12.34m, item.Price); Assert.Equal("Saved name", item.TicketTypeName); Assert.Equal(2, item.Quantity);
    }

    [Fact]
    public void Large_offer_does_not_limit_a_single_selected_type()
    {
        var offer = Enumerable.Range(0, 201).Select(i => Type("offer-" + i)).ToArray();
        Assert.Null(TicketSalesValidation.ValidateSelection(Event(), offer, new() { Selection("offer-200") }, 0, out var selected));
        Assert.Equal("offer-200", Assert.Single(selected).TicketTypeId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    [InlineData(int.MaxValue)]
    public void Invalid_quantities_are_rejected(int quantity) =>
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(), new[] { Type() }, new() { Selection(quantity: quantity) }, 0, out _));

    [Fact]
    public void Duplicate_type_rows_cannot_bypass_quantity_limits() =>
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(), new[] { Type() }, new() { Selection(), Selection() }, 0, out _));

    [Theory]
    [InlineData("foreign", EntityState.Active)]
    [InlineData("event", EntityState.Disabled)]
    public void Foreign_and_inactive_types_are_unavailable(string eventId, EntityState state)
    {
        var type = Type(); type.EventId = eventId; type.TicketTypeState = state;
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(), new[] { type }, new() { Selection() }, 0, out _));
    }

    [Fact]
    public void Aggregate_event_capacity_is_checked_against_settled_admissions() =>
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(true), new[] { Type() }, new() { Selection(quantity: 3) }, 4, out _));

    [Fact]
    public void Sold_quantities_reduce_available_stock()
    {
        var type = Type(quantity: 5); type.QuantitySold = 4;
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(), new[] { type }, new() { Selection(quantity: 2) }, 0, out _));
    }

    [Fact]
    public void Zero_stock_only_means_unlimited_on_an_uncapped_event()
    {
        Assert.Null(TicketSalesValidation.ValidateSelection(Event(), new[] { Type(quantity: 0) }, new() { Selection() }, 0, out _));
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(true), new[] { Type(quantity: 0) }, new() { Selection() }, 0, out _));
    }

    [Fact]
    public void Zero_total_is_not_silently_treated_as_a_paid_order() =>
        Assert.Equal("ZeroPrice", TicketSalesValidation.ValidateSelection(Event(), new[] { Type(price: 0) }, new() { Selection() }, 0, out _));

    [Fact]
    public void Zero_count_is_distinct_from_zero_price() =>
        Assert.Equal("ChooseFirst", TicketSalesValidation.ValidateSelection(Event(), new[] { Type() }, new() { Selection(quantity: 0) }, 0, out _));

    [Fact]
    public void Total_admissions_are_bounded_across_different_types()
    {
        var types = Enumerable.Range(0, 11).Select(i => Type("type" + i)).ToArray();
        var requested = types.Select(t => Selection(t.Id, 10)).ToList();
        Assert.Equal("SelectionChanged", TicketSalesValidation.ValidateSelection(Event(), types, requested, 0, out _));
    }

    [Fact]
    public void Recipients_remain_bound_to_type_and_admission_when_offer_order_changes()
    {
        var selected = new List<TicketSelectionViewModel> { Selection("b"), Selection("a", 2) };
        var contacts = new List<TicketSalesContactViewModel> { Contact("a", 1, "third@example.test"), Contact("b", 0, "first@example.test"), Contact("a", 0, "second@example.test") };
        Assert.Null(TicketSalesValidation.ValidateRecipients(selected, contacts, "individual", null!, out var result));
        Assert.Equal(new[] { "first@example.test", "second@example.test", "third@example.test" }, result.Select(c => c.Email));
        Assert.All(result, c => Assert.Equal("First", c.FirstName));
    }

    [Fact]
    public void Shared_delivery_uses_one_email_but_preserves_each_attendee_name()
    {
        var contacts = new List<TicketSalesContactViewModel> { Contact(index: 0), Contact(index: 1) };
        contacts[1].FirstName = "Second";
        Assert.Null(TicketSalesValidation.ValidateRecipients(new() { Selection(quantity: 2) }, contacts, "together", " all@example.test ", out var result));
        Assert.All(result, c => Assert.Equal("all@example.test", c.Email));
        Assert.Equal(new[] { "First", "Second" }, result.Select(c => c.FirstName));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    [InlineData("missing")]
    [InlineData("index")]
    public void Recipient_identity_must_be_a_complete_unique_permutation(string attack)
    {
        var contacts = new List<TicketSalesContactViewModel> { Contact(index: 0), Contact(index: 1) };
        if (attack == "duplicate") contacts[1].AdmissionIndex = 0;
        if (attack == "foreign") contacts[1].TicketTypeId = "another-event-type";
        if (attack == "missing") contacts.RemoveAt(1);
        if (attack == "index") contacts[1].AdmissionIndex = -1;
        Assert.Equal("ContactsInvalid", TicketSalesValidation.ValidateRecipients(new() { Selection(quantity: 2) }, contacts, "individual", null!, out _));
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("person@example.test\r\nBcc: another@example.test")]
    [InlineData("")]
    public void Invalid_or_header_injecting_addresses_are_rejected(string email) =>
        Assert.Equal("ContactsInvalid", TicketSalesValidation.ValidateRecipients(new() { Selection() }, new() { Contact(email: email) }, "individual", null!, out _));

    [Theory]
    [InlineData("FirstName")]
    [InlineData("LastName")]
    public void Required_names_are_checked_on_the_server(string field)
    {
        var contact = Contact();
        if (field == "FirstName") contact.FirstName = " "; else contact.LastName = " ";
        Assert.Equal("ContactsInvalid", TicketSalesValidation.ValidateRecipients(new() { Selection() }, new() { contact }, "individual", null!, out _));
    }

    [Fact]
    public void Oversized_drafts_are_rejected_before_session_storage()
    {
        var draft = new TicketSalesSubmission { TxnId = "server-nonce", Tickets = new() { Selection() }, ContactInfo = new() { Contact() } };
        Assert.True(TicketSalesValidation.HasBoundedShape(draft));
        draft.ContactInfo[0].FirstName = new string('x', 101);
        Assert.False(TicketSalesValidation.HasBoundedShape(draft));
        draft.ContactInfo = Enumerable.Range(0, 101).Select(_ => Contact()).ToList();
        Assert.False(TicketSalesValidation.HasBoundedShape(draft));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("sk")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("cs")]
    public void All_public_copy_keys_exist_in_every_supported_language(string language)
    {
        var english = new TicketSalesText("en").ClientCopy();
        var localized = new TicketSalesText(language).ClientCopy();
        Assert.Equal(english.Keys.Order(), localized.Keys.Order());
        Assert.All(localized, pair => { Assert.False(string.IsNullOrWhiteSpace(pair.Value)); });
        Assert.Equal(language, new TicketSalesText(language).Language);
    }

    [Fact]
    public void Culture_fallback_dates_and_currency_precision_are_explicit()
    {
        Assert.Equal("en", new TicketSalesText("unknown").Language);
        var text = new TicketSalesText("sk-SK");
        Assert.Equal("sk", text.Language);
        Assert.Contains("12,34", text.Money(12.34m, 2, "EUR"));
        Assert.Contains("0,00000001 BTC", text.Money(.00000001m, 8, "BTC"));
        Assert.EndsWith(" UTC", text.Date(new DateTime(2030, 11, 28, 8, 37, 0)));
        Assert.Contains("8:37", text.Date(new DateTime(2030, 11, 28, 8, 37, 0)));
    }
}
