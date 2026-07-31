using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.SatoshiTickets.Models.Api;

public class RecipientRequest
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    [Required]
    [EmailAddress]
    public string Email { get; set; }
}
