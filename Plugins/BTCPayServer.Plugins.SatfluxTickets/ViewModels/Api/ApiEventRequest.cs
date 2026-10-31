using BTCPayServer.Plugins.SatfluxTickets.Services;
using System;
using System.Collections.Generic;

namespace BTCPayServer.Plugins.SatfluxTickets.Models.Api;

public class ApiEventRequest
{
    private readonly HashSet<string> _specified = new(StringComparer.Ordinal);
    public bool Has(string property) => _specified.Contains(property);

    private string _title;
    public string Title { get => _title; set { _title = value; _specified.Add(nameof(Title)); } }
    private string _description;
    public string Description { get => _description; set { _description = value; _specified.Add(nameof(Description)); } }
    private string _eventType;
    public string EventType { get => _eventType; set { _eventType = value; _specified.Add(nameof(EventType)); } }
    private string _location;
    public string Location { get => _location; set { _location = value; _specified.Add(nameof(Location)); } }
    private string _timeZone;
    public string TimeZone { get => _timeZone; set { _timeZone = value; _specified.Add(nameof(TimeZone)); } }
    private string _startLocal;
    public string StartLocal { get => _startLocal; set { _startLocal = value; _specified.Add(nameof(StartLocal)); } }
    private string _endLocal;
    public string EndLocal { get => _endLocal; set { _endLocal = value; _specified.Add(nameof(EndLocal)); } }
    private DateTime? _startDate;
    public DateTime? StartDate { get => _startDate; set { _startDate = value; _specified.Add(nameof(StartDate)); } }
    private DateTime? _endDate;
    public DateTime? EndDate { get => _endDate; set { _endDate = value; _specified.Add(nameof(EndDate)); } }
    private string _currency;
    public string Currency { get => _currency; set { _currency = value; _specified.Add(nameof(Currency)); } }
    private string _redirectUrl;
    [TicketRedirectUrl]
    public string RedirectUrl { get => _redirectUrl; set { _redirectUrl = value; _specified.Add(nameof(RedirectUrl)); } }
    private string _emailSubject;
    public string EmailSubject { get => _emailSubject; set { _emailSubject = value; _specified.Add(nameof(EmailSubject)); } }
    private string _emailBody;
    public string EmailBody { get => _emailBody; set { _emailBody = value; _specified.Add(nameof(EmailBody)); } }
    private Guid? _bundledRaffleId;
    public Guid? BundledRaffleId { get => _bundledRaffleId; set { _bundledRaffleId = value; _specified.Add(nameof(BundledRaffleId)); } }
    private int? _bundledRaffleTicketsPerAdmission;
    public int? BundledRaffleTicketsPerAdmission { get => _bundledRaffleTicketsPerAdmission; set { _bundledRaffleTicketsPerAdmission = value; _specified.Add(nameof(BundledRaffleTicketsPerAdmission)); } }
}
