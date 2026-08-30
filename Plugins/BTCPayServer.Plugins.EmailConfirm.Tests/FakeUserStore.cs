#nullable enable
using BTCPayServer.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.EmailConfirm.Tests;

/// <summary>
/// Minimal in-memory user store: just enough for FindByIdAsync /
/// FindByEmailAsync / UpdateAsync as used by the controller.
/// </summary>
internal sealed class FakeUserStore : IUserStore<ApplicationUser>, IUserEmailStore<ApplicationUser>
{
    public Dictionary<string, ApplicationUser> Users { get; } = new();

    public int UpdateCalls { get; private set; }

    public FakeUserStore(IEnumerable<ApplicationUser> users)
    {
        foreach (var user in users)
            Users[user.Id] = user;
    }

    public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        => Task.FromResult(Users.GetValueOrDefault(userId));

    public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
        => Task.FromResult(Users.Values.FirstOrDefault(u =>
            string.Equals(u.NormalizedUserName ?? u.UserName, normalizedUserName, StringComparison.OrdinalIgnoreCase)));

    public Task<ApplicationUser?> FindByEmailAsync(string normalizedEmail, CancellationToken cancellationToken)
        => Task.FromResult(Users.Values.FirstOrDefault(u =>
            string.Equals(u.NormalizedEmail ?? u.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)));

    public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        UpdateCalls++;
        Users[user.Id] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.Id);

    public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.UserName);

    public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken)
    {
        user.UserName = userName;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.NormalizedUserName);

    public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken)
    {
        user.NormalizedUserName = normalizedName;
        return Task.CompletedTask;
    }

    public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        Users[user.Id] = user;
        return Task.FromResult(IdentityResult.Success);
    }

    public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        Users.Remove(user.Id);
        return Task.FromResult(IdentityResult.Success);
    }

    public Task SetEmailAsync(ApplicationUser user, string? email, CancellationToken cancellationToken)
    {
        user.Email = email;
        return Task.CompletedTask;
    }

    public Task<string?> GetEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.Email);

    public Task<bool> GetEmailConfirmedAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.EmailConfirmed);

    public Task SetEmailConfirmedAsync(ApplicationUser user, bool confirmed, CancellationToken cancellationToken)
    {
        user.EmailConfirmed = confirmed;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedEmailAsync(ApplicationUser user, CancellationToken cancellationToken)
        => Task.FromResult(user.NormalizedEmail ?? user.Email);

    public Task SetNormalizedEmailAsync(ApplicationUser user, string? normalizedEmail, CancellationToken cancellationToken)
    {
        user.NormalizedEmail = normalizedEmail;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }
}

/// <summary>Null logger for the UserManager constructor.</summary>
internal sealed class NullLogger : ILogger<UserManager<ApplicationUser>>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => false;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
    }
}
