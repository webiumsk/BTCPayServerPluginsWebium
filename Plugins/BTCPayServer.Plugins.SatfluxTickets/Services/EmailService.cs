#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Plugins.SatfluxTickets.Data;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace BTCPayServer.Plugins.SatfluxTickets.Services;

public class EmailService
{
    private readonly EmailSenderFactory _emailSender;
    private readonly Logging.Logs _logs;

    public EmailService(EmailSenderFactory emailSender, Logging.Logs logs)
    {
        _logs = logs;
        _emailSender = emailSender;
    }

    public async Task<bool> IsEmailSettingsConfigured(string storeId)
    {
        var emailSender = await _emailSender.GetEmailSender(storeId);
        return (await emailSender.GetEmailSettings() ?? new EmailSettings()).IsComplete();
    }

    private static string MaskEmail(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
            return "(none)";

        var at = address.IndexOf('@');
        if (at <= 0)
            return "****";

        return $"****{address[at..]}";
    }

    private static string ApplyTemplate(string? template, Event ticketEvent, Ticket ticket)
    {
        var emailBody = template ?? string.Empty;
        emailBody = emailBody
            .Replace("{{Title}}", ticketEvent.Title ?? string.Empty)
            .Replace("{{Location}}", ticketEvent.Location ?? string.Empty)
            .Replace("{{Name}}", $"{ticket.FirstName} {ticket.LastName}".Trim())
            .Replace("{{Email}}", ticket.Email ?? string.Empty)
            .Replace("{{Description}}", ticketEvent.Description ?? string.Empty)
            .Replace("{{EventDate}}", EventTimeZoneService.EmailDate(ticketEvent))
            .Replace("{{Currency}}", ticketEvent.Currency ?? string.Empty);

        return emailBody;
    }

    private async Task<EmailDispatchResult> SendBulkEmail(string storeId, IEnumerable<EmailRecipient> recipients)
    {
        var settings = await (await _emailSender.GetEmailSender(storeId)).GetEmailSettings();
        if (settings is null || !settings.IsComplete())
            return new EmailDispatchResult { IsSuccessful = false };

        var recipientGroups = GroupRecipients(recipients);
        if (recipientGroups.Count == 0)
            return new EmailDispatchResult { IsSuccessful = true };

        var failedRecipients = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(recipientGroups, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (tickets, _) =>
        {
            var recipient = tickets[0];
            using var client = await settings.CreateSmtpClient();
            try
            {
                using var message = TicketEmailMessageBuilder.Create(settings, tickets);
                await client.SendAsync(message);
            }
            catch (Exception ex)
            {
                failedRecipients.Add(recipient.Address.ToString());
                _logs.PayServer.LogError(ex, "Error sending email to: {MaskedEmail}", MaskEmail(recipient.Address.ToString()));
            }
            finally
            {
                if (client.IsConnected)
                    await client.DisconnectAsync(true);
            }
        });
        var failed = failedRecipients.ToList();
        return new EmailDispatchResult { IsSuccessful = failed.Count == 0, FailedRecipients = failed };
    }

    // Callers provide one order's tickets, or settled tickets from one store/event for reminders.
    // Never combine recipients from unrelated stores or events in the same batch.
    internal static List<EmailRecipient[]> GroupRecipients(IEnumerable<EmailRecipient> recipients)
        => recipients.GroupBy(recipient => recipient.Address.Address.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.ToArray()).ToList();


    private async Task<EmailDispatchResult> SendBulkEmail2(string storeId, IEnumerable<EmailRecipient> recipients)
    {
        var settings = await (await _emailSender.GetEmailSender(storeId)).GetEmailSettings();
        if (settings is null || !settings.IsComplete())
            return new EmailDispatchResult { IsSuccessful = false };

        var failedRecipients = new List<string>();
        var isSuccess = true;
        SmtpClient? client = null;
        try
        {
            client = await settings.CreateSmtpClient();
            foreach (var recipient in recipients)
            {
                try
                {
                    var message = new MimeMessage();
                    message.From.Add(MailboxAddress.Parse(settings.From));
                    message.To.Add(recipient.Address);
                    message.Subject = recipient.Subject;
                    message.Body = new TextPart("plain") { Text = recipient.MessageText };
                    await client.SendAsync(message);
                }
                catch (Exception ex)
                {
                    isSuccess = false;
                    failedRecipients.Add(recipient.Address.ToString());
                    _logs.PayServer.LogError(ex, "Error sending email to: {MaskedEmail}", MaskEmail(recipient.Address.ToString()));
                }
            }
        }
        catch (Exception ex)
        {
            _logs.PayServer.LogError(ex, "Failed to establish SMTP connection for bulk email");
            return new EmailDispatchResult { IsSuccessful = false, FailedRecipients = recipients.Select(r => r.Address.ToString()).ToList() };
        }
        finally
        {
            if (client != null)
            {
                try
                {
                    if (client.IsConnected)
                        await client.DisconnectAsync(true);
                }
                catch (Exception ex)
                {
                    _logs.PayServer.LogError(ex, "Error disconnecting SMTP client");
                }
                client.Dispose();
            }
        }
        return new EmailDispatchResult { IsSuccessful = isSuccess, FailedRecipients = failedRecipients };
    }

    public async Task<EmailDispatchResult> SendTicketRegistrationEmail(string storeId, Ticket ticket, Event ticketEvent)
    {
        var recipients = new List<EmailRecipient>
        {
            new()
            {
                Address = MailboxAddress.Parse(ticket.Email),
                Subject = ticketEvent.EmailSubject,
                MessageText = ApplyTemplate(ticketEvent.EmailBody, ticketEvent, ticket),
                TicketNumber = ticket.TicketNumber,
                TicketLink = ticket.QRCodeLink
            }
        };
        return await SendBulkEmail(storeId, recipients);
    }

    public async Task SendTicketRegistrationEmail(string storeId, IEnumerable<Ticket> tickets, Event ticketEvent)
    {
        var recipients = new List<EmailRecipient>();
        foreach (var ticket in tickets)
        {
            try
            {
                recipients.Add(new EmailRecipient
                {
                    Address = MailboxAddress.Parse(ticket.Email),
                    Subject = ticketEvent.EmailSubject,
                    MessageText = ApplyTemplate(ticketEvent.EmailBody, ticketEvent, ticket),
                    TicketNumber = ticket.TicketNumber,
                    TicketLink = ticket.QRCodeLink
                });
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogWarning(ex, "Invalid email for ticket {TicketId}: {MaskedEmail}",
                    ticket.Id, MaskEmail(ticket.Email));
            }
        }
        await SendBulkEmail(storeId, recipients);
    }

    public async Task<bool> SendReminderEmail(string storeId, IEnumerable<Ticket> tickets, Event ticketEvent, string reminderSubject, string reminderBody)
    {
        var recipients = new List<EmailRecipient>();
        var subject = !string.IsNullOrWhiteSpace(reminderSubject) ? reminderSubject : ticketEvent.EmailSubject;
        var bodyTemplate = !string.IsNullOrWhiteSpace(reminderBody) ? reminderBody : ticketEvent.EmailBody;
        foreach (var ticket in tickets)
        {
            try
            {
                recipients.Add(new EmailRecipient
                {
                    Address = MailboxAddress.Parse(ticket.Email),
                    Subject = subject,
                    MessageText = ApplyTemplate(bodyTemplate, ticketEvent, ticket),
                    TicketNumber = ticket.TicketNumber,
                    TicketLink = ticket.QRCodeLink
                });
            }
            catch (Exception ex)
            {
                _logs.PayServer.LogWarning(ex, "Invalid email for ticket {TicketId}: {MaskedEmail}",
                    ticket.Id, MaskEmail(ticket.Email));
                return false;
            }
        }
        var sendBultEmail = await SendBulkEmail(storeId, recipients);
        return sendBultEmail.IsSuccessful;
    }

    public string GetEmbeddedResourceContent(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var fullResourceName = assembly.GetManifestResourceNames()
                                       .FirstOrDefault(r => r.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));

        if (fullResourceName == null)
        {
            throw new FileNotFoundException($"Resource '{resourceName}' not found in assembly.");
        }
        using var stream = assembly.GetManifestResourceStream(fullResourceName);
        if (stream is null)
        {
            throw new FileNotFoundException(
                $"Resource stream for '{fullResourceName}' not found in assembly '{assembly.FullName}'.");
        }
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public class EmailRecipient
    {
        public MailboxAddress Address { get; set; }
        public string Subject { get; set; }
        public string MessageText { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string TicketLink { get; set; } = string.Empty;
    }

    public class EmailDispatchResult
    {
        public List<string> FailedRecipients { get; set; } = new();
        public bool IsSuccessful { get; set; }
    }
}
