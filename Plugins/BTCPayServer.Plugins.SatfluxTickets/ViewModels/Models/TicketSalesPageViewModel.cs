using System;
using System.Collections.Generic;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using BTCPayServer.Plugins.SatfluxTickets.Services;

namespace BTCPayServer.Plugins.SatfluxTickets.ViewModels;

public class TicketSalesPageViewModel : BaseSimpleTicketPublicViewModel
{
    public string EventId { get; set; }
    public string EventTitle { get; set; }
    public string Description { get; set; }
    public string EventImageUrl { get; set; }
    public string Location { get; set; }
    public EventType EventType { get; set; }
    public bool IsPhysical { get; set; }
    public bool MissingVenueTimeZone { get; set; }
    public Dictionary<string, TicketSalesVenueDates> VenueDates { get; set; } = new();
    public DateTime StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public string Currency { get; set; }
    public int CurrencyDecimals { get; set; }
    public string TxnId { get; set; }
    public List<TicketSalesTypeViewModel> TicketTypes { get; set; } = new();
    public List<TicketSalesContactViewModel> ContactInfo { get; set; } = new();
    public string DeliveryOption { get; set; } = "together";
    public string SharedEmail { get; set; }
    public string ErrorKey { get; set; }
    public bool ContactsOpen { get; set; }
    public bool CheckoutStarted { get; set; }
    public string CheckoutUrl { get; set; }
    public TicketSalesText Text { get; set; }
}

public class TicketSalesTypeViewModel
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Description { get; set; }
    public decimal Price { get; set; }
    public int Maximum { get; set; }
    public int Selected { get; set; }
}

public class TicketSalesContactViewModel
{
    public string TicketTypeId { get; set; }
    public int AdmissionIndex { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Email { get; set; }
}

public class TicketSalesSubmission
{
    public string Language { get; set; }
    public bool ContactsOpen { get; set; }
    public string TxnId { get; set; }
    public List<TicketSelectionViewModel> Tickets { get; set; } = new();
    public List<TicketSalesContactViewModel> ContactInfo { get; set; } = new();
    public string DeliveryOption { get; set; } = "together";
    public string SharedEmail { get; set; }
}

public class TicketSalesVenueDates
{
    public string Start { get; set; }
    public string End { get; set; }
    public string Label { get; set; }
}
