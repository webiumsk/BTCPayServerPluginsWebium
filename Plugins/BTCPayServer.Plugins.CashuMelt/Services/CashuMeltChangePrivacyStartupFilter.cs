using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace BTCPayServer.Plugins.CashuMelt.Services;

/// <summary>Set privacy headers even when authentication or content-type checks short-circuit MVC.</summary>
public sealed class CashuMeltChangePrivacyStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (IsChangePath(context.Request.Path.Value))
            {
                context.Response.OnStarting(() =>
                {
                    context.Response.Headers.CacheControl = "private, no-store, no-cache";
                    context.Response.Headers.Pragma = "no-cache";
                    context.Response.Headers.XContentTypeOptions = "nosniff";
                    return System.Threading.Tasks.Task.CompletedTask;
                });
            }
            await continuation();
        });
        next(app);
    };

    private static bool IsChangePath(string? path)
    {
        var parts = path?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || parts.Length is not (7 or 8)) return false;
        return Eq(parts[0], "api") && Eq(parts[1], "v1") && Eq(parts[2], "stores")
            && Eq(parts[4], "plugins") && Eq(parts[5], "cashumelt") && Eq(parts[6], "change")
            && (parts.Length == 7 || Eq(parts[7], "export") || Eq(parts[7], "verify"));
    }

    private static bool Eq(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
