using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.SepaInstantQr.Data;
using BTCPayServer.Plugins.SepaInstantQr.Data.Entities;
using BTCPayServer.Plugins.SepaInstantQr.Services;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Nop;
using BTCPayServer.Plugins.SepaInstantQr.Services.Confirmation.Fio;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.SepaInstantQr.Tests;

[CollectionDefinition("SEPA isolation database", DisableParallelization = true)]
public class SepaIsolationCollection : ICollectionFixture<SepaIsolationDatabase> { }

/// <summary>Never falls back to application configuration or a developer database.</summary>
public sealed class SepaIsolationDatabase : IAsyncLifetime
{
    public SepaDbContextFactory Factory { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var raw = Environment.GetEnvironmentVariable("SEPA_ISOLATION_TEST_CONNECTION");
        if (string.IsNullOrEmpty(raw))
            throw new InvalidOperationException("An explicitly disposable SEPA test connection is required.");
        var cs = new NpgsqlConnectionStringBuilder(raw);
        if (cs.Host != "127.0.0.1" || cs.Port != 5432 ||
            cs.Database != "satflux_sepa_isolation_qa" || cs.Username != "synthetic")
            throw new InvalidOperationException("Refusing a non-disposable SEPA database target.");
        await using var connection = new NpgsqlConnection(raw);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT current_database(), current_user", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        if (reader.GetString(0) != cs.Database || reader.GetString(1) != "synthetic")
            throw new InvalidOperationException("Resolved database identity does not match the disposable fixture.");
        await reader.DisposeAsync();
        Factory = new SepaDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = raw }));
        await using var context = Factory.CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public async Task ResetAsync()
    {
        await using var context = Factory.CreateContext();
        await context.SepaPaymentRequests.ExecuteDeleteAsync();
        await context.SepaStoreSettings.ExecuteDeleteAsync();
    }
}

[Collection("SEPA isolation database")]
public class SepaConfirmationIsolationTests
{
    private const string Store = "synthetic-store-a";
    private const string OtherStore = "synthetic-store-b";
    private const string OriginalIban = "SK4811000000002944116480";
    private const string OtherIban = "SK6807200002891987426353";
    private const string Reference = "QR-00000000000000000000000000000001";
    private readonly SepaIsolationDatabase _database;

    public SepaConfirmationIsolationTests(SepaIsolationDatabase database) => _database = database;

    private sealed class RecordingSpy : SepaPaymentRecorder
    {
        private int _calls;
        public int Calls => _calls;
        public Queue<bool> Outcomes { get; } = new();
        public Action<SepaPaymentRequest>? BeforeOutcome { get; set; }

        public RecordingSpy() : base(null!, null!, null!, null!, NullLogger<SepaPaymentRecorder>.Instance) { }

        public override Task<bool> RecordAsync(SepaPaymentRequest request, decimal amount,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            BeforeOutcome?.Invoke(request);
            return Task.FromResult(Outcomes.Count == 0 || Outcomes.Dequeue());
        }
    }

    private async Task<(SepaMatchingService Matcher, RecordingSpy Recorder)> ArrangeAsync(
        string backend = "fio", decimal amountDue = 12.50m)
    {
        await _database.ResetAsync();
        await using var context = _database.Factory.CreateContext();
        context.SepaPaymentRequests.Add(new SepaPaymentRequest
        {
            Reference = Reference,
            InvoiceId = "synthetic-invoice-a",
            StoreId = Store,
            Iban = OriginalIban,
            AmountDue = amountDue,
            Currency = "EUR",
            Backend = backend,
            State = SepaPaymentRequestState.Pending,
        });
        await context.SaveChangesAsync();
        var recorder = new RecordingSpy();
        return (new SepaMatchingService(_database.Factory, recorder,
            NullLogger<SepaMatchingService>.Instance), recorder);
    }

    private static ConfirmedPayment Credit(string? iban = OriginalIban, bool origin = true,
        string reference = Reference, decimal amount = 12.50m, string currency = "EUR", string dedup = "synthetic-event-1")
        => new(reference, amount, currency, "{\"synthetic\":true}", dedup,
            CreditorIban: iban, BankOriginVerified: origin);

    private async Task<SepaPaymentRequest> ReadAsync(string reference = Reference)
    {
        await using var context = _database.Factory.CreateContext();
        return await context.SepaPaymentRequests.AsNoTracking().SingleAsync(r => r.Reference == reference);
    }

    [Fact]
    public async Task Foreign_store_reference_never_mutates_the_request_or_calls_the_recorder()
    {
        var (matcher, recorder) = await ArrangeAsync();
        var result = await matcher.ProcessAsync(OtherStore, "fio", Credit(), 0m);

        Assert.Equal(MatchOutcome.UnknownReference, result);
        var row = await ReadAsync();
        Assert.Equal(SepaPaymentRequestState.Pending, row.State);
        Assert.Null(row.DedupKey);
        Assert.Null(row.ReviewReason);
        Assert.Null(row.RawConfirmationJson);
        Assert.Null(row.ConfirmedAt);
        Assert.Equal(0, recorder.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SK4811000000002944116481")]
    [InlineData(OtherIban)]
    public async Task Missing_invalid_or_foreign_recipient_is_reviewed_without_consuming_the_event(string? iban)
    {
        var (matcher, recorder) = await ArrangeAsync();
        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "fio", Credit(iban), 0m));
        var row = await ReadAsync();
        Assert.Equal(SepaPaymentRequestState.ManualReview, row.State);
        Assert.Null(row.DedupKey);
        Assert.Null(row.ConfirmedAt);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Legacy_email_without_verified_origin_does_not_settle_or_consume_the_event()
    {
        var (matcher, recorder) = await ArrangeAsync("bmail");
        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "bmail", Credit(origin: false), 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(0, recorder.Calls);
    }

    [Theory]
    [InlineData("bmail", "fio")]
    [InlineData("fio", "bmail")]
    [InlineData("manual", "fio")]
    public async Task Recognized_complementary_bank_sources_can_confirm_the_original_account(string backend, string source)
    {
        var (matcher, recorder) = await ArrangeAsync(backend);
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, source, Credit(), 0m));
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(OriginalIban, (await ReadAsync()).Iban);
    }

    [Fact]
    public async Task An_unrecognized_source_cannot_settle_even_with_plausible_payment_fields()
    {
        var (matcher, recorder) = await ArrangeAsync();
        Assert.NotEqual(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "unregistered-bank", Credit(), 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Corrected_proof_with_the_same_event_can_settle_after_review()
    {
        var (matcher, recorder) = await ArrangeAsync();
        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "fio", Credit(OtherIban), 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Equal(SepaPaymentRequestState.Confirmed, (await ReadAsync()).State);
        Assert.Equal(1, recorder.Calls);
    }

    [Fact]
    public async Task Current_settings_cannot_replace_the_original_account_snapshot()
    {
        var (matcher, recorder) = await ArrangeAsync();
        await using (var context = _database.Factory.CreateContext())
        {
            context.SepaStoreSettings.Add(new SepaStoreSettings { StoreId = Store, Iban = OtherIban });
            await context.SaveChangesAsync();
        }
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Equal(OriginalIban, (await ReadAsync()).Iban);
        Assert.Equal(1, recorder.Calls);
    }

    [Theory]
    [InlineData(12.49, "EUR")]
    [InlineData(12.50, "CZK")]
    public async Task Incorrect_amount_or_currency_cannot_reach_the_recorder(double amount, string currency)
    {
        var (matcher, recorder) = await ArrangeAsync();
        Assert.Equal(MatchOutcome.ManualReview,
            await matcher.ProcessAsync(Store, "fio", Credit(amount: (decimal)amount, currency: currency), 0m));
        Assert.Equal(0, recorder.Calls);
        Assert.Null((await ReadAsync()).ConfirmedAt);
    }

    [Fact]
    public async Task Duplicate_delivery_records_once_and_keeps_the_first_confirmation()
    {
        var (matcher, recorder) = await ArrangeAsync();
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        var confirmed = await ReadAsync();
        Assert.Equal(MatchOutcome.Duplicate, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        var repeated = await ReadAsync();
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(confirmed.ConfirmedAt, repeated.ConfirmedAt);
        Assert.Equal(confirmed.DedupKey, repeated.DedupKey);
    }

    [Fact]
    public async Task One_bank_event_cannot_record_payments_for_two_invoice_references()
    {
        var (matcher, recorder) = await ArrangeAsync();
        const string secondReference = "QR-00000000000000000000000000000002";
        await using (var context = _database.Factory.CreateContext())
        {
            context.SepaPaymentRequests.Add(new SepaPaymentRequest
            {
                Reference = secondReference, StoreId = Store, InvoiceId = "synthetic-invoice-b",
                Iban = OriginalIban, Backend = "fio", AmountDue = 12.50m, Currency = "EUR",
            });
            await context.SaveChangesAsync();
        }
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Equal(MatchOutcome.Duplicate,
            await matcher.ProcessAsync(Store, "fio", Credit(reference: secondReference), 0m));
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(SepaPaymentRequestState.Pending, (await ReadAsync(secondReference)).State);
        Assert.Null((await ReadAsync(secondReference)).DedupKey);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_the_same_reference_record_exactly_once()
    {
        var (matcher, recorder) = await ArrangeAsync();
        var results = await Task.WhenAll(
            matcher.ProcessAsync(Store, "fio", Credit(), 0m),
            matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Contains(MatchOutcome.Settled, results);
        Assert.Contains(MatchOutcome.Duplicate, results);
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(SepaPaymentRequestState.Confirmed, (await ReadAsync()).State);
    }

    [Fact]
    public async Task Concurrent_references_cannot_both_record_the_same_bank_event()
    {
        var (matcher, recorder) = await ArrangeAsync();
        const string secondReference = "QR-00000000000000000000000000000002";
        await using (var context = _database.Factory.CreateContext())
        {
            context.SepaPaymentRequests.Add(new SepaPaymentRequest
            {
                Reference = secondReference, StoreId = Store, InvoiceId = "synthetic-invoice-b",
                Iban = OriginalIban, Backend = "fio", AmountDue = 12.50m, Currency = "EUR",
            });
            await context.SaveChangesAsync();
        }
        var results = await Task.WhenAll(
            matcher.ProcessAsync(Store, "fio", Credit(), 0m),
            matcher.ProcessAsync(Store, "fio", Credit(reference: secondReference), 0m));
        Assert.Contains(MatchOutcome.Settled, results);
        Assert.Contains(MatchOutcome.Duplicate, results);
        Assert.Equal(1, recorder.Calls);
        var first = await ReadAsync();
        var second = await ReadAsync(secondReference);
        Assert.True((first.State == SepaPaymentRequestState.Confirmed) != (second.State == SepaPaymentRequestState.Confirmed));
    }

    [Fact]
    public async Task Recorder_failure_allows_a_retry_with_the_identical_bank_event()
    {
        var (matcher, recorder) = await ArrangeAsync();
        recorder.Outcomes.Enqueue(false);
        recorder.Outcomes.Enqueue(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Equal(2, recorder.Calls);
    }

    [Fact]
    public async Task A_retry_after_external_success_uses_the_same_invoice_payment_identity()
    {
        var (matcher, recorder) = await ArrangeAsync();
        var externalPaymentIds = new HashSet<string>();
        var loseFirstResponse = true;
        recorder.BeforeOutcome = request =>
        {
            externalPaymentIds.Add(request.Reference);
            if (loseFirstResponse)
            {
                loseFirstResponse = false;
                throw new IOException("Synthetic response loss after recording the stable payment id.");
            }
        };
        await Assert.ThrowsAsync<IOException>(() => matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(SepaPaymentRequestState.Pending, (await ReadAsync()).State);
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "fio", Credit(), 0m));
        Assert.Single(externalPaymentIds);
        Assert.Contains(Reference, externalPaymentIds);
        Assert.Equal(2, recorder.Calls);
    }

    private sealed class FioBankFixture : HttpMessageHandler, IHttpClientFactory
    {
        private readonly string _statement;
        public List<string> Paths { get; } = new();
        public FioBankFixture(string statement) => _statement = statement;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            var body = path.Contains("/last/", StringComparison.Ordinal) ? _statement
                : path.Contains("/set-last-id/", StringComparison.Ordinal) ? "{}"
                : throw new InvalidOperationException("Unexpected synthetic FIO request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task Actual_fio_poll_rewinds_after_a_false_recorder_and_retries_the_same_movement()
    {
        var (matcher, recorder) = await ArrangeAsync();
        recorder.Outcomes.Enqueue(false);
        recorder.Outcomes.Enqueue(true);
        var statement = JsonSerializer.Serialize(new
        {
            accountStatement = new
            {
                info = new { iban = OriginalIban, currency = "EUR", idLastDownload = 7L },
                transactionList = new
                {
                    transaction = new[] { new
                    {
                        column22 = new { value = 9L }, column1 = new { value = 12.50m },
                        column14 = new { value = "EUR" }, column27 = new { value = Reference },
                    } },
                },
            },
        });
        using var bank = new FioBankFixture(statement);
        var config = new SepaConfigService(_database.Factory, new EphemeralDataProtectionProvider());
        var settings = new SepaStoreSettings { StoreId = Store, Iban = OriginalIban, ConfirmationBackend = "fio" };
        config.ApplyCredentials(settings, new SepaBackendCredentials { FioToken = new string('a', 64) });
        var poller = new SepaPollingHostedService(_database.Factory, config, new NopNotificationProcessor(),
            new FioApiClient(bank), new FioTransactionProcessor(), matcher,
            NullLogger<SepaPollingHostedService>.Instance);
        var poll = typeof(SepaPollingHostedService).GetMethod("PollFioStoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(poll);
        await (Task)poll.Invoke(poller, new object[] { settings, CancellationToken.None })!;
        Assert.Contains(bank.Paths, path => path.Contains("/set-last-id/", StringComparison.Ordinal) && path.EndsWith("/7/", StringComparison.Ordinal));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(SepaPaymentRequestState.Pending, (await ReadAsync()).State);

        await (Task)poll.Invoke(poller, new object[] { settings, CancellationToken.None })!;
        Assert.Equal(SepaPaymentRequestState.Confirmed, (await ReadAsync()).State);
        Assert.Equal(2, recorder.Calls);
        Assert.Equal(3, bank.Paths.Count);
    }

    [Fact]
    public async Task Ambiguous_fio_memo_is_reviewed_without_recording_or_consuming_the_bank_event()
    {
        var (matcher, recorder) = await ArrangeAsync();
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            accountStatement = new
            {
                info = new { iban = OriginalIban, currency = "EUR" },
                transactionList = new
                {
                    transaction = new[] { new
                    {
                        column22 = new { value = 42L }, column1 = new { value = 12.50m },
                        column14 = new { value = "EUR" },
                        column16 = new { value = Reference + " or QR-00000000000000000000000000000002" },
                    } },
                },
            },
        }));
        var payment = Assert.Single(new FioTransactionProcessor().Parse(doc)).Payment;

        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "fio", payment, 0m));
        Assert.Equal(0, recorder.Calls);
        var row = await ReadAsync();
        Assert.Equal(SepaPaymentRequestState.ManualReview, row.State);
        Assert.Null(row.DedupKey);
        Assert.Null(row.ConfirmedAt);
    }

    [Fact]
    public async Task Nop_hash_without_an_account_field_is_checked_against_the_invoice_snapshot()
    {
        var (matcher, recorder) = await ArrangeAsync("nop-rest");
        var proof = Credit(iban: null, origin: true) with
        {
            NopIntegrity = new NopPaymentIntegrity("12.50",
                NopNotificationProcessor.ComputeHash(OriginalIban, "12.50", "EUR", Reference)),
        };
        Assert.Equal(MatchOutcome.Settled, await matcher.ProcessAsync(Store, "nop-rest", proof, 0m));
        Assert.Equal(1, recorder.Calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public async Task Nonpositive_nop_credit_cannot_settle_with_large_tolerance_and_preserves_corrected_retry(string amountRaw)
    {
        var (matcher, recorder) = await ArrangeAsync("nop-rest", amountDue: 1m);
        var processor = new NopNotificationProcessor();
        ConfirmedPayment Notification(string raw)
        {
            var result = processor.ToConfirmedPayment(new NopNotification(
                "ACCC", raw, "EUR", Reference,
                NopNotificationProcessor.ComputeHash(OriginalIban, raw, "EUR", Reference),
                OriginalIban, "synthetic-credit-event", "{\"synthetic\":true}"));
            Assert.NotNull(result);
            return result;
        }

        var rejected = Notification(amountRaw);
        Assert.Equal(MatchOutcome.ManualReview,
            await matcher.ProcessAsync(Store, "nop-rest", rejected, 10m));
        var row = await ReadAsync();
        Assert.Equal(SepaPaymentRequestState.ManualReview, row.State);
        Assert.Equal("automatic confirmation requires a positive credited amount", row.ReviewReason);
        Assert.Null(row.DedupKey);
        Assert.Null(row.ConfirmedAt);
        Assert.Equal(0, recorder.Calls);

        // A positive credit below the amount due remains eligible under the
        // merchant's tolerance; the rejected event did not reserve its identity.
        var corrected = Notification("0.50");
        Assert.Equal(rejected.DedupKey, corrected.DedupKey);
        Assert.Equal(MatchOutcome.Settled,
            await matcher.ProcessAsync(Store, "nop-rest", corrected, 10m));
        row = await ReadAsync();
        Assert.Equal(SepaPaymentRequestState.Confirmed, row.State);
        Assert.NotNull(row.DedupKey);
        Assert.NotNull(row.ConfirmedAt);
        Assert.Equal(1, recorder.Calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(OriginalIban)]
    [InlineData(OtherIban)]
    public async Task A_valid_nop_hash_for_a_different_account_never_settles(string? observedIban)
    {
        var (matcher, recorder) = await ArrangeAsync("nop-rest");
        var proof = Credit(observedIban, origin: true) with
        {
            NopIntegrity = new NopPaymentIntegrity("12.50",
                NopNotificationProcessor.ComputeHash(OtherIban, "12.50", "EUR", Reference)),
        };
        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "nop-rest", proof, 0m));
        Assert.Null((await ReadAsync()).DedupKey);
        Assert.Equal(0, recorder.Calls);
    }

    [Fact]
    public async Task Nop_creditor_field_must_agree_even_when_the_hash_matches_the_original_invoice()
    {
        var (matcher, recorder) = await ArrangeAsync("nop-rest");
        var proof = Credit(OtherIban, origin: true) with
        {
            NopIntegrity = new NopPaymentIntegrity("12.50",
                NopNotificationProcessor.ComputeHash(OriginalIban, "12.50", "EUR", Reference)),
        };
        Assert.Equal(MatchOutcome.ManualReview, await matcher.ProcessAsync(Store, "nop-rest", proof, 0m));
        Assert.Equal(0, recorder.Calls);
        Assert.Null((await ReadAsync()).DedupKey);
    }

    [Fact]
    public async Task Magic_manual_source_name_does_not_bypass_automatic_proof_checks()
    {
        var (matcher, recorder) = await ArrangeAsync("manual");
        Assert.NotEqual(MatchOutcome.Settled,
            await matcher.ProcessAsync(Store, "manual:synthetic-actor", Credit(iban: null, origin: false), 0m));
        Assert.Equal(0, recorder.Calls);
        Assert.Null((await ReadAsync()).DedupKey);
    }

    [Fact]
    public async Task Explicit_manual_confirmation_is_scoped_and_preserves_the_original_request()
    {
        var (matcher, recorder) = await ArrangeAsync("manual");
        Assert.Equal(MatchOutcome.UnknownReference,
            await matcher.ConfirmManuallyAsync(OtherStore, "synthetic-actor", Reference));
        Assert.Equal(0, recorder.Calls);
        Assert.Equal(SepaPaymentRequestState.Pending, (await ReadAsync()).State);
        Assert.Equal(MatchOutcome.Settled,
            await matcher.ConfirmManuallyAsync(Store, "synthetic-actor", Reference));
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(OriginalIban, (await ReadAsync()).Iban);
    }

    [Fact]
    public async Task Explicit_manual_confirmation_preserves_zero_due_behavior()
    {
        var (matcher, recorder) = await ArrangeAsync("manual", amountDue: 0m);

        Assert.Equal(MatchOutcome.Settled,
            await matcher.ConfirmManuallyAsync(Store, "synthetic-actor", Reference));
        Assert.Equal(1, recorder.Calls);
        Assert.Equal(SepaPaymentRequestState.Confirmed, (await ReadAsync()).State);
    }
}
