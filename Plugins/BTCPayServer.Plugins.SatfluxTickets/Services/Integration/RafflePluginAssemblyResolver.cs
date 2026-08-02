#nullable enable
using System;
using System.Linq;
using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SatfluxTickets.Services.Integration;

internal static class RafflePluginAssemblyResolver
{
    private const string RafflePluginIdentifier = "BTCPayServer.Plugins.BTCPayRaffle";

    public static Assembly? GetRafflePluginAssembly(IServiceProvider serviceProvider)
    {
        foreach (var plugin in serviceProvider.GetServices<IBTCPayServerPlugin>())
        {
            if (string.Equals(plugin.Identifier, RafflePluginIdentifier, StringComparison.Ordinal))
                return plugin.GetType().Assembly;
        }

        return AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, RafflePluginIdentifier, StringComparison.Ordinal));
    }

    public static object? GetRaffleService(IServiceProvider serviceProvider, string typeFullName)
    {
        var assembly = GetRafflePluginAssembly(serviceProvider);
        if (assembly is null)
            return null;

        var serviceType = assembly.GetType(typeFullName, throwOnError: false);
        if (serviceType is null)
            return null;

        return serviceProvider.GetService(serviceType);
    }
}
