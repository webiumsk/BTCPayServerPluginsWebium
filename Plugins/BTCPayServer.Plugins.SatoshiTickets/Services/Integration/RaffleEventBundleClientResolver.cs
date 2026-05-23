#nullable enable
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.SatoshiTickets.Services.Integration;

public sealed class RaffleEventBundleClientProvider
{
    private readonly IServiceProvider _serviceProvider;
    private IRaffleEventBundleClient? _client;
    private bool _resolved;

    public RaffleEventBundleClientProvider(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>Resolved on first use so BTCPay Raffle can load after Satoshi Tickets.</summary>
    public IRaffleEventBundleClient? Client
    {
        get
        {
            if (!_resolved)
            {
                _client = RaffleEventBundleClientResolver.TryResolve(_serviceProvider);
                _resolved = true;
            }
            return _client;
        }
    }
}

internal static class RaffleEventBundleClientResolver
{
    private const string RaffleAssemblyName = "BTCPayServer.Plugins.BTCPayRaffle";
    private const string BundleServiceTypeName = "BTCPayServer.Plugins.BTCPayRaffle.Services.IRaffleEventBundleService";
    private const string BundleResultTypeName = "BTCPayServer.Plugins.BTCPayRaffle.Services.RaffleEventBundleResult";

    public static IRaffleEventBundleClient? TryResolve(IServiceProvider serviceProvider)
    {
        var raffleAssembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => string.Equals(a.GetName().Name, RaffleAssemblyName, StringComparison.Ordinal));
        if (raffleAssembly is null)
            return null;

        var serviceType = raffleAssembly.GetType(BundleServiceTypeName, throwOnError: false);
        if (serviceType is null)
            return null;

        var implementation = serviceProvider.GetService(serviceType);
        if (implementation is null)
            return null;

        var resultType = raffleAssembly.GetType(BundleResultTypeName, throwOnError: false);
        if (resultType is null)
            return null;

        var implType = implementation.GetType();
        if (implType.GetMethod("ValidateBundledRaffleAsync", BindingFlags.Public | BindingFlags.Instance) is null
            || implType.GetMethod("AllocateForEventOrderAsync", BindingFlags.Public | BindingFlags.Instance) is null)
            return null;

        return new ReflectionRaffleEventBundleClient(implementation, serviceType, resultType);
    }
}

internal sealed class ReflectionRaffleEventBundleClient : IRaffleEventBundleClient
{
    private readonly object _target;
    private readonly MethodInfo? _validateMethod;
    private readonly MethodInfo? _allocateMethod;
    private readonly Type _resultType;

    public ReflectionRaffleEventBundleClient(object target, Type serviceType, Type resultType)
    {
        _target = target;
        _resultType = resultType;
        var implType = target.GetType();
        _validateMethod = implType.GetMethod(
            "ValidateBundledRaffleAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(string), typeof(Guid)],
            null);
        _allocateMethod = implType.GetMethod(
            "AllocateForEventOrderAsync",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [typeof(string), typeof(Guid), typeof(int), typeof(string), typeof(string), typeof(string), typeof(string)],
            null);
    }

    public async Task<(bool Ok, string? Error)> ValidateBundledRaffleAsync(string storeId, Guid raffleId)
    {
        if (_validateMethod is null)
            return (false, "Raffle bundle validation is unavailable");

        try
        {
            var result = await InvokeAsync(_validateMethod, [storeId, raffleId]).ConfigureAwait(false);
            return ReadValueTupleBoolString(result);
        }
        catch (Exception)
        {
            return (false, "Raffle bundle validation is unavailable");
        }
    }

    public async Task<RaffleBundleAllocationResult> AllocateForEventOrderAsync(
        string storeId,
        Guid raffleId,
        int count,
        string buyerEmail,
        string? buyerName,
        string eventOrderId,
        string baseUrl)
    {
        if (_allocateMethod is null)
            return new RaffleBundleAllocationResult { Success = false, Error = "Raffle bundle allocation is unavailable" };

        try
        {
            var result = await InvokeAsync(_allocateMethod,
                [storeId, raffleId, count, buyerEmail, buyerName, eventOrderId, baseUrl]).ConfigureAwait(false);
            return MapResult(result);
        }
        catch (Exception)
        {
            return new RaffleBundleAllocationResult { Success = false, Error = "Raffle bundle allocation is unavailable" };
        }
    }

    private async Task<object?> InvokeAsync(MethodInfo method, object?[] args)
    {
        object? taskObj;
        try
        {
            taskObj = method.Invoke(_target, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }

        if (taskObj is not Task task)
            throw new InvalidOperationException("Raffle bundle service did not return a Task");

        await task.ConfigureAwait(false);
        return taskObj.GetType().GetProperty("Result")?.GetValue(taskObj);
    }

    private static (bool Ok, string? Error) ReadValueTupleBoolString(object? result)
    {
        if (result is null)
            return (false, "Empty raffle validation response");

        // ValueTuple uses public fields Item1/Item2, not properties.
        var type = result.GetType();
        var okField = type.GetField("Item1") ?? type.GetField("Ok");
        if (okField is null)
            return (false, "Invalid raffle validation response");

        if (okField.GetValue(result) is not bool okBool)
            return (false, "Invalid raffle validation response");

        var errField = type.GetField("Item2") ?? type.GetField("Error");
        var errorValue = errField?.GetValue(result) as string;
        return (okBool, errorValue);
    }

    private RaffleBundleAllocationResult MapResult(object? result)
    {
        if (result is null)
            return new RaffleBundleAllocationResult { Success = false, Error = "Empty raffle bundle response" };

        var type = result.GetType();
        var successProp = type.GetProperty("Success");
        var isNewProp = type.GetProperty("IsNew");
        var ticketsProp = type.GetProperty("TicketsAllocated");
        var errorProp = type.GetProperty("Error");
        if (successProp is null || isNewProp is null || ticketsProp is null || errorProp is null)
            return new RaffleBundleAllocationResult { Success = false, Error = "Invalid raffle bundle response" };

        return new RaffleBundleAllocationResult
        {
            Success = successProp.GetValue(result) is bool success && success,
            IsNew = isNewProp.GetValue(result) is bool isNew && isNew,
            TicketsAllocated = ticketsProp.GetValue(result) is int count ? count : 0,
            Error = errorProp.GetValue(result) as string
        };
    }
}
