using System;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.BTCPayRaffle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.BTCPayRaffle.Tests;

internal sealed class InMemoryRaffleDbFactory : RaffleDbContextFactory
{
    private readonly string _dbName = $"raffle-test-{Guid.NewGuid():N}";

    public InMemoryRaffleDbFactory()
        : base(Options.Create(new DatabaseOptions { ConnectionString = "inmemory" }))
    {
    }

    public override RaffleDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = null)
    {
        var options = new DbContextOptionsBuilder<RaffleDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new RaffleDbContext(options);
    }
}
