using BTCPayServer.Plugins.Emails.Services;
using BTCPayServer.Plugins.SatfluxTickets.Services;
using MimeKit;
using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

public class TicketEmailMessageBuilderTests
{
    [Fact]
    public void Builds_html_ticket_card_with_embedded_qr_and_plain_text_fallback()
    {
        const string ticketNumber = "EVT-0007-261020-00042";
        const string ticketLink = "https://tickets.example.test/ticket?id=42&view=public";
        var settings = new EmailSettings { From = "tickets@example.test" };

        using var message = TicketEmailMessageBuilder.Create(
            settings,
            MailboxAddress.Parse("attendee@example.test"),
            "Your ticket",
            "Welcome to the event.",
            ticketNumber,
            ticketLink);

        var html = message.BodyParts.OfType<TextPart>().Single(part => part.IsHtml).Text;
        var text = message.BodyParts.OfType<TextPart>().Single(part => part.IsPlain).Text;
        var image = message.BodyParts.OfType<MimePart>().Single(part => part.ContentType.MimeType == "image/png");

        Assert.Contains("text-align:center", html);
        Assert.Contains("<strong>00042</strong>", html);
        Assert.Contains($"cid:{image.ContentId}", html);
        Assert.Contains("View your ticket online", html);
        Assert.Contains("Ticket number: 00042", text);
        Assert.DoesNotContain(ticketNumber, html);
        Assert.DoesNotContain(ticketNumber, text);
        Assert.Contains(ticketLink, text);

        using var png = new MemoryStream();
        var imageContent = image.Content ?? throw new InvalidOperationException("The ticket QR image has no MIME content.");
        imageContent.DecodeTo(png);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png.ToArray()[..8]);
        Assert.Equal(TicketQrCode.CreatePng(ticketNumber, pixelsPerModule: 8), png.ToArray());
    }

    [Theory]
    [InlineData("LEGACY42")]
    [InlineData("LEGACY42-")]
    public void Preserves_legacy_numbers_without_a_nonempty_suffix(string ticketNumber)
    {
        using var message = TicketEmailMessageBuilder.Create(
            new EmailSettings { From = "tickets@example.test" },
            MailboxAddress.Parse("attendee@example.test"),
            "Your ticket", "Welcome.", ticketNumber, null);

        var html = message.BodyParts.OfType<TextPart>().Single(part => part.IsHtml).Text;
        var text = message.BodyParts.OfType<TextPart>().Single(part => part.IsPlain).Text;
        Assert.Contains($"<strong>{ticketNumber}</strong>", html);
        Assert.Contains($"Ticket number: {ticketNumber}", text);
    }

    [Fact]
    public void Html_encodes_ticket_data_and_omits_qr_when_number_is_missing()
    {
        var settings = new EmailSettings { From = "tickets@example.test" };
        using var message = TicketEmailMessageBuilder.Create(
            settings,
            MailboxAddress.Parse("attendee@example.test"),
            "Your ticket",
            "Welcome <attendee>.",
            "",
            "https://tickets.example.test/ticket?a=1&b=2");

        var html = message.BodyParts.OfType<TextPart>().Single(part => part.IsHtml).Text;

        Assert.Contains("Welcome &lt;attendee&gt;.", html);
        Assert.Contains("a=1&amp;b=2", html);
        Assert.DoesNotContain("cid:", html);
        Assert.DoesNotContain(message.BodyParts.OfType<MimePart>(), part => part.ContentType.MimeType == "image/png");
    }

    [Fact]
    public void Same_mailbox_receives_all_personalized_tickets_with_distinct_inline_qrs()
    {
        var tickets = new[]
        {
            Ticket("Ada <buyer@example.test>", "FIRST42", "Welcome Ada."),
            Ticket("Grace <BUYER@example.test>", "SECOND42", "Welcome Grace.")
        };
        var group = Assert.Single(EmailService.GroupRecipients(tickets));
        Assert.Equal(2, group.Length);
        using var message = TicketEmailMessageBuilder.Create(new EmailSettings { From = "tickets@example.test" }, group);

        // Exercise the MIME structure that is actually serialized for SMTP.
        using var serialized = new MemoryStream();
        message.WriteTo(serialized);
        serialized.Position = 0;
        using var delivered = MimeMessage.Load(serialized);
        var html = delivered.BodyParts.OfType<TextPart>().Single(part => part.IsHtml).Text;
        var text = delivered.BodyParts.OfType<TextPart>().Single(part => part.IsPlain).Text;
        var images = delivered.BodyParts.OfType<MimePart>().Where(part => part.ContentType.MimeType == "image/png").ToArray();

        Assert.Single(delivered.To);
        Assert.Empty(delivered.Cc);
        Assert.Empty(delivered.Bcc);
        Assert.Equal(2, images.Length);
        Assert.Equal(2, images.Select(image => image.ContentId).Distinct().Count());
        foreach (var (ticket, index) in tickets.Select((ticket, index) => (ticket, index)))
        {
            var shortNumber = ticket.TicketNumber.Split('-').Last();
            Assert.Contains($"<strong>{shortNumber}</strong>", html);
            Assert.Contains($"Ticket number: {shortNumber}", text);
            Assert.Contains(ticket.MessageText, html);
            Assert.Contains(ticket.MessageText, text);
            Assert.Contains(ticket.TicketLink, text);

            var image = Assert.Single(images, candidate => candidate.FileName == $"ticket-qr-{index + 1}.png");
            Assert.Contains($"cid:{image.ContentId}", html);
            using var png = new MemoryStream();
            (image.Content ?? throw new InvalidOperationException("Missing QR content.")).DecodeTo(png);
            Assert.Equal(TicketQrCode.CreatePng(ticket.TicketNumber, pixelsPerModule: 8), png.ToArray());
        }
    }

    [Fact]
    public void Different_mailboxes_receive_only_their_own_ticket_codes_and_qrs()
    {
        var tickets = new[]
        {
            Ticket("one@example.test", "ALPHA1", "For first recipient."),
            Ticket("two@example.test", "BETA1", "For second recipient."),
            Ticket("one@example.test", "ALPHA2", "Another first-recipient ticket.")
        };
        var groups = EmailService.GroupRecipients(tickets);
        Assert.Equal(2, groups.Count);
        foreach (var group in groups)
        {
            using var message = TicketEmailMessageBuilder.Create(new EmailSettings { From = "tickets@example.test" }, group);
            var addressee = Assert.IsType<MailboxAddress>(Assert.Single(message.To));
            var text = message.BodyParts.OfType<TextPart>().Single(part => part.IsPlain).Text;
            var html = message.BodyParts.OfType<TextPart>().Single(part => part.IsHtml).Text;
            Assert.Equal(group[0].Address.Address, addressee.Address);
            Assert.Equal(group.Length, message.BodyParts.OfType<MimePart>().Count(part => part.ContentType.MimeType == "image/png"));
            foreach (var ticket in tickets)
            {
                var shortNumber = ticket.TicketNumber.Split('-').Last();
                if (group.Contains(ticket))
                {
                    Assert.Contains($"Ticket number: {shortNumber}", text);
                    Assert.Contains($"<strong>{shortNumber}</strong>", html);
                }
                else
                {
                    Assert.DoesNotContain(shortNumber, text);
                    Assert.DoesNotContain(shortNumber, html);
                    Assert.DoesNotContain(ticket.MessageText, text);
                }
            }
        }
    }

    [Fact]
    public void Builder_rejects_mixed_recipients_even_if_caller_does_not_group_them()
    {
        var tickets = new[] { Ticket("one@example.test", "ONE", ""), Ticket("two@example.test", "TWO", "") };
        Assert.Throws<ArgumentException>(() => TicketEmailMessageBuilder.Create(
            new EmailSettings { From = "tickets@example.test" }, tickets));
    }

    [Fact]
    public void Empty_recipient_list_creates_no_groups_and_cannot_create_an_email()
    {
        Assert.Empty(EmailService.GroupRecipients(Array.Empty<EmailService.EmailRecipient>()));
        Assert.Throws<ArgumentException>(() => TicketEmailMessageBuilder.Create(
            new EmailSettings { From = "tickets@example.test" }, Array.Empty<EmailService.EmailRecipient>()));
    }

    private static EmailService.EmailRecipient Ticket(string address, string code, string message)
        => new()
        {
            Address = MailboxAddress.Parse(address),
            Subject = "Your tickets",
            MessageText = message,
            TicketNumber = $"EVT-0007-261020-{code}",
            TicketLink = $"https://tickets.example.test/ticket/{code}"
        };
}
