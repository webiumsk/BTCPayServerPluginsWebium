using Xunit;

namespace BTCPayServer.Plugins.SatfluxTickets.Tests;

// Shared disposable schema migrations must finish before another fixture seeds it.
// Individual cases still explicitly exercise concurrent merchant operations.
[CollectionDefinition("Tickets disposable PostgreSQL", DisableParallelization = true)]
public sealed class TicketDatabaseCollection;
