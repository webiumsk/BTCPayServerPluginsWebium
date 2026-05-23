#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.BTCPayRaffle.Data.Entities;
using BTCPayServer.Plugins.Emails.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BTCPayServer.Plugins.BTCPayRaffle.Services;

public class RaffleTicketEmailService
{
    private readonly EmailSenderFactory _emailSenderFactory;
    private readonly RaffleBuyerWalletTokenService _walletTokens;
    private readonly Logs _logs;

    public RaffleTicketEmailService(
        EmailSenderFactory emailSenderFactory,
        RaffleBuyerWalletTokenService walletTokens,
        Logs logs)
    {
        _emailSenderFactory = emailSenderFactory;
        _walletTokens = walletTokens;
        _logs = logs;
    }

    public async Task SendTicketsEmailAsync(
        Guid raffleId,
        string raffleName,
        string buyerEmail,
        string? buyerName,
        IReadOnlyList<RaffleTicket> tickets,
        string baseUrl,
        string? receiptUrl = null,
        bool manualAllocation = false,
        string? introOverride = null,
        string? storeId = null)
    {
        if (tickets.Count == 0 || string.IsNullOrWhiteSpace(buyerEmail))
            return;

        try
        {
            var (sender, settings, settingsSource) = await ResolveEmailSenderAsync(storeId);
            if (settings?.IsComplete() != true)
            {
                _logs.PayServer.LogWarning(
                    "Raffle ticket email skipped: email settings not configured (store={StoreId}, tried={Source})",
                    storeId ?? "(none)", settingsSource);
                return;
            }

            if (!RafflePublicUrlHelper.TryGetTrustedOrigin(baseUrl, out var origin))
            {
                _logs.PayServer.LogWarning(
                    "Raffle ticket email skipped: invalid base URL {BaseUrl} (store={StoreId})",
                    baseUrl, storeId ?? "(server)");
                return;
            }

            var (walletToken, _) = _walletTokens.CreateToken(raffleId, buyerEmail);
            var walletPath = $"/raffle/{raffleId}/my?token={Uri.EscapeDataString(walletToken)}";
            var walletUrl = RafflePublicUrlHelper.BuildPath(origin, walletPath);
            var ticketNumbers = string.Join(", ", tickets.Select(t => $"#{t.TicketNumber}"));
            var subject = $"Your tickets — {raffleName}";
            var intro = introOverride ?? (manualAllocation
                ? "Your ticket(s) have been allocated!"
                : "Your ticket purchase was confirmed!");
            var body = BuildEmailHtml(raffleName, intro, tickets, walletUrl, receiptUrl, origin);

            sender.SendEmail(
                new MailboxAddress(buyerName ?? buyerEmail, buyerEmail),
                subject,
                body);
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogWarning(ex, "Could not send ticket email to {MaskedEmail}",
                RaffleBuyerDisplay.MaskEmail(buyerEmail));
        }
    }

    private static string BuildEmailHtml(
        string raffleName,
        string introLine,
        IReadOnlyList<RaffleTicket> tickets,
        string walletUrl,
        string? receiptUrl,
        Uri origin)
    {
        var safeWalletUrl = RafflePublicUrlHelper.HtmlAttribute(walletUrl);
        var sb = new StringBuilder();
        sb.Append($@"<!DOCTYPE html><html><body style=""font-family:sans-serif;max-width:600px;margin:0 auto;padding:20px"">
<div style=""background:#f8f9fa;border-radius:12px;padding:24px;text-align:center"">
  <div style=""font-size:48px"">🎟️</div>
  <h2 style=""margin:8px 0"">{System.Net.WebUtility.HtmlEncode(raffleName)}</h2>
  <p style=""color:#666"">{System.Net.WebUtility.HtmlEncode(introLine)}</p>
</div>
<div style=""margin:24px 0"">
  <h3>Your ticket numbers:</h3>");

        foreach (var t in tickets)
        {
            var verifyUrl = RafflePublicUrlHelper.HtmlAttribute(
                RafflePublicUrlHelper.BuildPath(origin, $"/raffle/ticket/{t.Id}"));
            sb.Append($@"
  <div style=""border:2px solid #dee2e6;border-radius:8px;padding:12px 16px;margin:8px 0;display:flex;align-items:center;justify-content:space-between"">
    <span style=""font-size:28px;font-weight:900;font-family:monospace;color:#333"">#{t.TicketNumber}</span>
    <a href=""{verifyUrl}"" style=""font-size:12px;color:#0d6efd"">Verify</a>
  </div>");
        }

        sb.Append($@"
</div>
<div style=""text-align:center;margin:24px 0"">
  <a href=""{safeWalletUrl}"" style=""background:#0d6efd;color:#fff;text-decoration:none;padding:12px 28px;border-radius:8px;font-weight:600;display:inline-block"">
    View all my tickets
  </a>
  <p style=""margin:12px 0 0;font-size:13px;color:#666"">Same email on later purchases? Everything appears on one page.</p>");

        if (!string.IsNullOrEmpty(receiptUrl)
            && Uri.TryCreate(receiptUrl, UriKind.Absolute, out var receiptUri)
            && (receiptUri.Scheme == Uri.UriSchemeHttp || receiptUri.Scheme == Uri.UriSchemeHttps)
            && receiptUri.Host.Length > 0)
        {
            var safeReceiptUrl = RafflePublicUrlHelper.HtmlAttribute(receiptUri.ToString());
            sb.Append($@"
  <p style=""margin:8px 0 0""><a href=""{safeReceiptUrl}"" style=""font-size:13px;color:#0d6efd"">Receipt for this purchase only</a></p>");
        }

        sb.Append($@"
</div>
<p style=""color:#999;font-size:12px;text-align:center"">Keep this link private — it shows all tickets for your email on this raffle.</p>
</body></html>");

        return sb.ToString();
    }

    private async Task<(IEmailSender Sender, EmailSettings? Settings, string Source)> ResolveEmailSenderAsync(string? storeId)
    {
        if (!string.IsNullOrEmpty(storeId))
        {
            var storeSender = await _emailSenderFactory.GetEmailSender(storeId);
            var storeSettings = await storeSender.GetEmailSettings();
            if (storeSettings?.IsComplete() == true)
                return (storeSender, storeSettings, "store");
        }

        var serverSender = await _emailSenderFactory.GetEmailSender(null);
        var serverSettings = await serverSender.GetEmailSettings();
        return (serverSender, serverSettings, "server");
    }
}
