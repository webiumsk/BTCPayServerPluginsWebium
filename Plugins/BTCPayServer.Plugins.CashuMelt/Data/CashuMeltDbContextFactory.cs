using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.CashuMelt.Data;

public class CashuMeltDbContextFactory : BaseDbContextFactory<CashuMeltDbContext>
{
    public CashuMeltDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.CashuMelt")
    {
    }

    public override CashuMeltDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = default)
    {
        var builder = new DbContextOptionsBuilder<CashuMeltDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new CashuMeltDbContext(builder.Options);
    }
}
