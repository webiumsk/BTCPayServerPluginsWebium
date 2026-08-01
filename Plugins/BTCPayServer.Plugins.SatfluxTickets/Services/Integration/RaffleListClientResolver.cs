#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SatfluxTickets.Services.Integration;

public sealed record RaffleOption(Guid Id, string Name);

public sealed class RaffleListClientProvider
{
    private readonly IServiceProvider _serviceProvider;

    public RaffleListClientProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task<IReadOnlyList<RaffleOption>> GetOpenRafflesAsync(string storeId)
    {
        return await RaffleListClientResolver.GetOpenRafflesAsync(_serviceProvider, storeId);
    }

    public bool IsRafflePluginAvailable =>
        RaffleListClientResolver.IsRafflePluginAvailable(_serviceProvider);
}

internal static class RaffleListClientResolver
{
    private const string RaffleServiceTypeName = "BTCPayServer.Plugins.BTCPayRaffle.Services.RaffleService";

    public static bool IsRafflePluginAvailable(IServiceProvider serviceProvider)
    {
        return TryGetRaffleService(serviceProvider) is not null;
    }

    public static async Task<IReadOnlyList<RaffleOption>> GetOpenRafflesAsync(
        IServiceProvider serviceProvider,
        string storeId)
    {
        var service = TryGetRaffleService(serviceProvider);
        if (service is null)
            return Array.Empty<RaffleOption>();

        var method = service.GetType().GetMethod(
            "GetRafflesForStoreAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(string)],
            null);
        if (method is null)
            return Array.Empty<RaffleOption>();

        try
        {
            var taskObj = method.Invoke(service, [storeId]);
            if (taskObj is not Task task)
                return Array.Empty<RaffleOption>();

            await task.ConfigureAwait(false);
            var result = taskObj.GetType().GetProperty("Result")?.GetValue(taskObj);
            if (result is not System.Collections.IEnumerable raffles)
                return Array.Empty<RaffleOption>();

            var options = new List<RaffleOption>();
            foreach (var raffle in raffles)
            {
                if (raffle is null)
                    continue;

                var type = raffle.GetType();
                var status = type.GetProperty("Status")?.GetValue(raffle);
                if (!IsOpenStatus(status))
                    continue;

                if (type.GetProperty("Id")?.GetValue(raffle) is not Guid id)
                    continue;

                var name = type.GetProperty("Name")?.GetValue(raffle) as string ?? id.ToString();
                options.Add(new RaffleOption(id, name));
            }

            return options.OrderBy(o => o.Name).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<RaffleOption>();
        }
    }

    private static object? TryGetRaffleService(IServiceProvider serviceProvider)
    {
        return RafflePluginAssemblyResolver.GetRaffleService(serviceProvider, RaffleServiceTypeName);
    }

    private static bool IsOpenStatus(object? status)
    {
        if (status is null)
            return false;

        if (status is Enum enumValue)
            return string.Equals(enumValue.ToString(), "Open", StringComparison.Ordinal);

        return string.Equals(status.ToString(), "Open", StringComparison.Ordinal);
    }
}
