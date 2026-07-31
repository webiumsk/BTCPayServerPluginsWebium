using System;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Options;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace BTCPayServer.Plugins.SepaInstantQr.Data;

public class SepaDbContextFactory : BaseDbContextFactory<SepaDbContext>
{
    public SepaDbContextFactory(IOptions<DatabaseOptions> options)
        : base(options, "BTCPayServer.Plugins.SepaInstantQr")
    {
    }

    public override SepaDbContext CreateContext(Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptionsAction = default)
    {
        var builder = new DbContextOptionsBuilder<SepaDbContext>();
        ConfigureBuilder(builder, npgsqlOptionsAction);
        return new SepaDbContext(builder.Options);
    }
}

/// <summary>Design-time factory for `dotnet ef migrations add ...`.</summary>
public class SepaDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SepaDbContext>
{
    public SepaDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<SepaDbContext>();
        builder.UseNpgsql("User ID=postgres;Host=127.0.0.1;Port=39372;Database=designtimebtcpay");
        return new SepaDbContext(builder.Options);
    }
}
