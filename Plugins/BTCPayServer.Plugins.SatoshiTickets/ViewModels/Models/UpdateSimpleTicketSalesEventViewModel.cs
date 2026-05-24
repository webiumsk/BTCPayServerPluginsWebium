using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Microsoft.AspNetCore.Http;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.SatoshiTickets.Data;
using BTCPayServer.Plugins.SatoshiTickets.Services;
using BTCPayServer.Plugins.SatoshiTickets.Services.Integration;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace BTCPayServer.Plugins.SatoshiTickets.ViewModels;

public class UpdateSimpleTicketSalesEventViewModel : IValidatableObject
{
    public string EventId { get; set; }
    public string StoreId { get; set; }
    public string Title { get; set; }

    [Display(Name = "Event Type")]
    public EventType EventType { get; set; }
    public List<SelectListItem> EventTypes { get; set; }
    public string Description { get; set; }

    [Display(Name = "Event Image URL")]
    public string EventImageUrl { get; set; }

    [Display(Name = "Event Image URL")]
    [JsonIgnore]
    public IFormFile EventImageFile { get; set; }

    [Display(Name = "Event location or URL")]
    public string Location { get; set; }

    [Display(Name = "Event Start Date")]
    public DateTime StartDate { get; set; } = DateTime.UtcNow.Date;

    [Display(Name = "Event End Date")]
    public DateTime? EndDate { get; set; }
    public string StoreDefaultCurrency { get; set; }
    public string Currency { get; set; }

    [Display(Name = "Email Subject")]
    public string EmailSubject { get; set; }

    [Display(Name = "Email Body")]
    public string EmailBody { get; set; }

    [Display(Name = "Redirect Url after ticket purchase")]
    public string RedirectUrl { get; set; }

    [Display(Name = "Enable Reminder to be sent for this event")]
    public bool ReminderEnabled { get; set; }

    [Display(Name = "Days before event to send reminder")]
    [Range(1, 365, ErrorMessage = "Must be between 1 and 365 days")]
    public int? ReminderDaysBeforeEvent { get; set; }

    [Range(0, EventRaffleBundleRequestValidator.MaxTicketsPerAdmission,
        ErrorMessage = "Bundled raffle tickets per admission must be between 0 and 20")]
    public int BundledRaffleTicketsPerAdmission { get; set; }
    public Guid? BundledRaffleId { get; set; }
    public bool RafflePluginAvailable { get; set; }
    public List<RaffleOption> OpenRaffles { get; set; } = new();
    public string? BundledRaffleName { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (BundledRaffleTicketsPerAdmission > 0
            && (!BundledRaffleId.HasValue || BundledRaffleId.Value == Guid.Empty))
        {
            yield return new ValidationResult(
                "Select an open raffle when including raffle tickets per admission",
                [nameof(BundledRaffleId)]);
        }

        if (BundledRaffleId is { } raffleId && raffleId != Guid.Empty
            && BundledRaffleTicketsPerAdmission <= 0)
        {
            yield return new ValidationResult(
                "Set raffle tickets per admission when a raffle is selected",
                [nameof(BundledRaffleTicketsPerAdmission)]);
        }

        if (BundledRaffleTicketsPerAdmission > 0 && !RafflePluginAvailable)
        {
            yield return new ValidationResult(
                "Event raffle bundles require BTCPay Raffle plugin 1.3.1 or newer on this server. Upgrade the Raffle plugin, or set raffle tickets per admission to 0.",
                [nameof(BundledRaffleId)]);
        }
    }

    public string? GetFirstBundleValidationError()
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateProperty(
            BundledRaffleTicketsPerAdmission,
            new ValidationContext(this) { MemberName = nameof(BundledRaffleTicketsPerAdmission) },
            results);
        results.AddRange(Validate(new ValidationContext(this)));
        return results.FirstOrDefault()?.ErrorMessage;
    }
}
