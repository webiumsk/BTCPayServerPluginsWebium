#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using BTCPayServer.Plugins.Emails.Services;
using MimeKit;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

internal static class TicketEmailMessageBuilder
{
    public static MimeMessage Create(
        EmailSettings settings,
        InternetAddress recipient,
        string subject,
        string? messageText,
        string? ticketNumber,
        string? ticketLink)
        => Create(settings, new[]
        {
            new EmailService.EmailRecipient
            {
                Address = recipient as MailboxAddress
                    ?? throw new ArgumentException("A ticket email requires one mailbox.", nameof(recipient)),
                Subject = subject,
                MessageText = messageText ?? string.Empty,
                TicketNumber = ticketNumber ?? string.Empty,
                TicketLink = ticketLink ?? string.Empty
            }
        });

    public static MimeMessage Create(EmailSettings settings, IReadOnlyList<EmailService.EmailRecipient> tickets)
    {
        if (tickets.Count == 0)
            throw new ArgumentException("A ticket email requires at least one ticket.", nameof(tickets));

        var recipient = tickets[0];
        foreach (var ticket in tickets)
        {
            if (!StringComparer.OrdinalIgnoreCase.Equals(recipient.Address.Address.Trim(), ticket.Address.Address.Trim()))
                throw new ArgumentException("A ticket email cannot combine different recipients.", nameof(tickets));
        }

        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        message.To.Add(recipient.Address);
        message.Subject = recipient.Subject;

        var bodyBuilder = new BodyBuilder();
        var textSections = new List<string>();
        var htmlSections = new List<string>();

        for (var index = 0; index < tickets.Count; index++)
        {
            var ticket = tickets[index];
            var displayNumber = GetDisplayNumber(ticket.TicketNumber);
            var textBody = ticket.MessageText ?? string.Empty;
            string? contentId = null;

            if (!string.IsNullOrWhiteSpace(ticket.TicketNumber))
            {
                textBody += $"\n\nTicket number: {displayNumber}";
                contentId = $"{Guid.NewGuid():N}@satflux-tickets";
                bodyBuilder.LinkedResources.Add(new MimePart("image", "png")
                {
                    Content = new MimeContent(new MemoryStream(TicketQrCode.CreatePng(ticket.TicketNumber, pixelsPerModule: 8))),
                    ContentId = contentId,
                    ContentDisposition = new ContentDisposition(ContentDisposition.Inline),
                    ContentTransferEncoding = ContentEncoding.Base64,
                    FileName = $"ticket-qr-{index + 1}.png"
                });
            }
            if (!string.IsNullOrWhiteSpace(ticket.TicketLink))
                textBody += $"\nView your ticket: {ticket.TicketLink}";

            textSections.Add(textBody);
            htmlSections.Add(CreateHtmlSection(ticket.MessageText, displayNumber, ticket.TicketLink, contentId));
        }

        bodyBuilder.TextBody = string.Join("\n\n---\n\n", textSections);
        var sections = string.Join("<hr style=\"margin:32px 0;border:0;border-top:1px solid #ddd;\">", htmlSections);
        bodyBuilder.HtmlBody = $"""
               <!doctype html>
               <html><body style="margin:0;padding:24px;font-family:Arial,Helvetica,sans-serif;color:#222;line-height:1.5;">
                 {sections}
               </body></html>
               """;

        message.Body = bodyBuilder.ToMessageBody();
        return message;
    }

    private static string? GetDisplayNumber(string? ticketNumber)
    {
        if (string.IsNullOrWhiteSpace(ticketNumber))
            return null;

        var separator = ticketNumber.LastIndexOf('-');
        return separator >= 0 && separator < ticketNumber.Length - 1
            ? ticketNumber[(separator + 1)..]
            : ticketNumber;
    }

    private static string CreateHtmlSection(string? messageText, string? ticketNumber, string? ticketLink, string? contentId)
    {
        var ticketSection = string.IsNullOrWhiteSpace(ticketNumber)
            ? string.Empty
            : $"""
              <div style="margin:24px auto;text-align:center;">
                <p style="margin:0 0 16px;text-align:center;font-size:22px;font-weight:700;">Ticket number<br><strong>{HtmlEncoder.Default.Encode(ticketNumber)}</strong></p>
                <img src="cid:{HtmlEncoder.Default.Encode(contentId ?? string.Empty)}" alt="QR code for ticket {HtmlEncoder.Default.Encode(ticketNumber)}" width="220" style="display:block;width:220px;max-width:100%;height:auto;margin:0 auto;" />
              </div>
              """;
        var ticketLinkSection = string.IsNullOrWhiteSpace(ticketLink)
            ? string.Empty
            : $"<p><a href=\"{HtmlEncoder.Default.Encode(ticketLink)}\">View your ticket online</a></p>";
        var encodedMessage = HtmlEncoder.Default.Encode(messageText ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal)
            .Replace("\n", "<br>\n", StringComparison.Ordinal);

        return $"""
               <div>
                 {ticketSection}
                 <div>{encodedMessage}</div>
                 {ticketLinkSection}
               </div>
               """;
    }
}
