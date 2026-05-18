#nullable enable
using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.BTCPayRaffle.Data;

public class RaffleDbContextFactory : BaseDbContextFactory<RaffleDbContext>
{
    public const string Schema = "BTCPayServer.Plugins.BTCPayRaffle";

    public RaffleDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, Schema)
    {
    }

    public override RaffleDbContext CreateContext(
        Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = default)
    {
        var builder = new DbContextOptionsBuilder<RaffleDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new RaffleDbContext(builder.Options);
    }
}
