using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SemanticAgentBroker.Api.Data;
using SemanticAgentBroker.Shared.Configuration;
using SemanticAgentBroker.Shared.Domain;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace SemanticAgentBroker.Api.Features.Auth;

public class SessionInfo
{
    public string Token { get; set; } = string.Empty;
    public Guid UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string IPAddress { get; set; } = string.Empty;
    public string BrowserId { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
}

public class AuthService
{
    public static readonly string ServerInstanceId = Guid.NewGuid().ToString();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentSystemOptions _options;
    private readonly ILogger<AuthService> _logger;
    private readonly PasswordHasher<User> _passwordHasher;
    private const int IpLockStripeCount = 128;
    private readonly SemaphoreSlim[] _ipLockStripes = Enumerable.Range(0, IpLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private const int UserLockStripeCount = 128;
    private readonly SemaphoreSlim[] _userLockStripes = Enumerable.Range(0, UserLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    private static readonly ConcurrentDictionary<string, DateTime> _lastActivityUpdates = new();

    private SemaphoreSlim GetIpLock(string ipAddress)
    {
        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(ipAddress);
        return _ipLockStripes[hash % IpLockStripeCount];
    }

    private SemaphoreSlim GetUserLock(string username)
    {
        var hash = (uint)StringComparer.OrdinalIgnoreCase.GetHashCode(username);
        return _userLockStripes[hash % UserLockStripeCount];
    }

    public AuthService(
        IServiceScopeFactory scopeFactory,
        IOptions<AgentSystemOptions> options,
        ILogger<AuthService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
        _passwordHasher = new PasswordHasher<User>();
    }

    public async Task<string?> LoginAsync(string username, string password, string ipAddress, string browserId)
    {
        var userLock = GetUserLock(username);
        var ipLock = GetIpLock(ipAddress);

        await userLock.WaitAsync();
        try
        {
            await ipLock.WaitAsync();
            try
            {
            using var scope = _scopeFactory.CreateScope();
            var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

        // 1. Anti-bruteforce check
        var recentFails = await authDb.FailedLoginAttempts
            .Where(f => f.IPAddress == ipAddress && f.Username == username)
            .FirstOrDefaultAsync();

        var totalUserFails = await authDb.FailedLoginAttempts
            .Where(f => f.Username == username)
            .ToListAsync();
        var userAttempts = totalUserFails.Sum(f => f.Attempts);

        if (recentFails != null && recentFails.Attempts >= _options.MaxFailedAttempts)
        {
            if (recentFails.LockoutEnd.HasValue && recentFails.LockoutEnd.Value > DateTime.UtcNow)
            {
                _logger.LogWarning("IP {IP} / User {User} is locked out until {Time}", ipAddress, username, recentFails.LockoutEnd.Value);
                throw new UnauthorizedAccessException("Too many failed attempts. Try again later.");
            }
            else if (recentFails.LockoutEnd.HasValue && recentFails.LockoutEnd.Value <= DateTime.UtcNow)
            {
                // Lockout expired, reset
                recentFails.Attempts = 0;
                recentFails.LockoutEnd = null;
                await authDb.SaveChangesAsync();
                userAttempts = totalUserFails.Where(f => f != recentFails).Sum(f => f.Attempts);
            }
        }
        else if (userAttempts >= _options.MaxFailedAttempts)
        {
            var activeLockout = totalUserFails.FirstOrDefault(f => f.LockoutEnd.HasValue && f.LockoutEnd.Value > DateTime.UtcNow);
            if (activeLockout != null)
            {
                _logger.LogWarning("User {User} is locked out until {Time}", username, activeLockout.LockoutEnd!.Value);
                throw new UnauthorizedAccessException("Too many failed attempts. Try again later.");
            }
            else
            {
                bool allExpired = totalUserFails.All(f => !f.LockoutEnd.HasValue || f.LockoutEnd.Value <= DateTime.UtcNow);
                if (allExpired && totalUserFails.Any(f => f.LockoutEnd.HasValue))
                {
                    authDb.FailedLoginAttempts.RemoveRange(totalUserFails);
                    await authDb.SaveChangesAsync();
                    recentFails = null;
                    userAttempts = 0;
                }
                else
                {
                    // No lockout end was set (e.g. concurrent failed attempts across different IPs).
                    // Set a lockout on the highest-attempt record so the lockout has a defined expiration.
                    var failRecord = totalUserFails.OrderByDescending(f => f.Attempts).First();
                    failRecord.LockoutEnd = DateTime.UtcNow.AddMinutes(_options.LockoutMinutes);
                    await authDb.SaveChangesAsync();
                    _logger.LogWarning("User {User} locked out until {Time} due to aggregate failed attempts", username, failRecord.LockoutEnd.Value);
                    throw new UnauthorizedAccessException("Too many failed attempts. Try again later.");
                }
            }
        }

        // 2. Validate Credentials
        var user = await authDb.Users.FirstOrDefaultAsync(u => u.Username == username);
        bool isValid = false;

        if (user != null)
        {
            var result = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
            if (result == PasswordVerificationResult.Success)
            {
                isValid = true;
            }
            else if (result == PasswordVerificationResult.SuccessRehashNeeded)
            {
                isValid = true;
                // Auto-upgrade hash if settings changed
                user.PasswordHash = _passwordHasher.HashPassword(user, password);
                await authDb.SaveChangesAsync();
            }
        }

        if (!isValid)
        {
            // Record failure
            if (recentFails == null)
            {
                recentFails = new FailedLoginAttempt { IPAddress = ipAddress, Username = username, Attempts = 1 };
                authDb.FailedLoginAttempts.Add(recentFails);
            }
            else
            {
                recentFails.Attempts++;
                if (recentFails.Attempts >= _options.MaxFailedAttempts)
                {
                    recentFails.LockoutEnd = DateTime.UtcNow.AddMinutes(_options.LockoutMinutes);
                }
            }

            var updatedUserAttempts = userAttempts + 1;
            if (updatedUserAttempts >= _options.MaxFailedAttempts && !recentFails.LockoutEnd.HasValue)
            {
                recentFails.LockoutEnd = DateTime.UtcNow.AddMinutes(_options.LockoutMinutes);
            }

            await authDb.SaveChangesAsync();
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        // Success - clear all failed attempts for this username across all IPs (and any untagged fails for current IP)
        var userFailsToClear = await authDb.FailedLoginAttempts
            .Where(f => f.Username == username || (string.IsNullOrEmpty(f.Username) && f.IPAddress == ipAddress))
            .ToListAsync();
        if (userFailsToClear.Count > 0)
        {
            authDb.FailedLoginAttempts.RemoveRange(userFailsToClear);
            await authDb.SaveChangesAsync();
        }

        // 3. Generate Session and Persist SHA-256 hash to tokens.db
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));
        var tokenHash = HashToken(token);
        var sessionRecord = new UserSessionRecord
        {
            Token = tokenHash,
            UserId = user!.Id,
            Username = user.Username,
            Role = user.Role,
            IPAddress = ipAddress,
            BrowserId = browserId,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddDays(_options.SessionDays),
            LastActivityAt = DateTime.UtcNow
        };

        tokenDb.Sessions.Add(sessionRecord);
        await tokenDb.SaveChangesAsync();

        return token;
    }
    finally
    {
        ipLock.Release();
    }
    }
    finally
    {
        userLock.Release();
    }
}

    internal static string HashToken(string token)
    {
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static bool IsIpAddressMatch(string storedIp, string currentIp)
    {
        if (string.Equals(storedIp, currentIp, StringComparison.OrdinalIgnoreCase))
            return true;

        if (System.Net.IPAddress.TryParse(storedIp, out var parsedStored) &&
            System.Net.IPAddress.TryParse(currentIp, out var parsedCurrent))
        {
            if (System.Net.IPAddress.IsLoopback(parsedStored) && System.Net.IPAddress.IsLoopback(parsedCurrent))
            {
                return true;
            }

            if (parsedStored.IsIPv4MappedToIPv6) parsedStored = parsedStored.MapToIPv4();
            if (parsedCurrent.IsIPv4MappedToIPv6) parsedCurrent = parsedCurrent.MapToIPv4();

            return parsedStored.Equals(parsedCurrent);
        }

        return false;
    }

    public async Task LogoutAsync(string ipAddress, string browserId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

        var sessions = await tokenDb.Sessions
            .Where(s => s.BrowserId == browserId)
            .ToListAsync(ct);

        var toRemove = sessions.Where(s => IsIpAddressMatch(s.IPAddress, ipAddress)).ToList();
        if (toRemove.Count > 0)
        {
            foreach (var s in toRemove)
            {
                _lastActivityUpdates.TryRemove(s.Token, out _);
            }
            tokenDb.Sessions.RemoveRange(toRemove);
            await tokenDb.SaveChangesAsync(ct);
        }
    }

    public async Task<bool> LogoutByTokenAsync(string token, string ipAddress, string browserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return false;

        using var scope = _scopeFactory.CreateScope();
        var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

        var tokenHash = HashToken(token);
        var session = await tokenDb.Sessions.FirstOrDefaultAsync(s => s.Token == tokenHash, ct);
        if (session == null)
            return false;

        if (!IsIpAddressMatch(session.IPAddress, ipAddress) || session.BrowserId != browserId)
            return false;

        _lastActivityUpdates.TryRemove(tokenHash, out _);
        tokenDb.Sessions.Remove(session);
        await tokenDb.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SessionInfo?> ValidateTokenAsync(string token, string ipAddress, string browserId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        using var scope = _scopeFactory.CreateScope();
        var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

        var tokenHash = HashToken(token);
        var session = await tokenDb.Sessions.FirstOrDefaultAsync(s => s.Token == tokenHash, ct);
        if (session != null)
        {
            if (session.ExpiresAt < DateTime.UtcNow)
            {
                _lastActivityUpdates.TryRemove(tokenHash, out _);
                try
                {
                    tokenDb.Sessions.Remove(session);
                    await tokenDb.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    // Concurrency: Another concurrent request already deleted this expired token record
                }
                return null;
            }

            // Must match IP and Browser ID
            if (IsIpAddressMatch(session.IPAddress, ipAddress) && session.BrowserId == browserId)
            {
                // Debounce LastActivityAt persistence: only save if > 60 seconds have elapsed
                var now = DateTime.UtcNow;
                if (now - session.LastActivityAt > TimeSpan.FromSeconds(60))
                {
                    bool shouldSave = false;
                    _lastActivityUpdates.AddOrUpdate(
                        tokenHash,
                        _ =>
                        {
                            shouldSave = true;
                            return now;
                        },
                        (_, lastSaved) =>
                        {
                            if (now - lastSaved > TimeSpan.FromSeconds(60))
                            {
                                shouldSave = true;
                                return now;
                            }
                            return lastSaved;
                        });

                    if (shouldSave)
                    {
                        session.LastActivityAt = now;
                        try
                        {
                            await tokenDb.SaveChangesAsync(ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Failed to update LastActivityAt for active session.");
                        }
                    }
                }

                return new SessionInfo
                {
                    Token = token,
                    UserId = session.UserId,
                    Username = session.Username,
                    Role = session.Role,
                    IPAddress = session.IPAddress,
                    BrowserId = session.BrowserId,
                    ExpiresAt = session.ExpiresAt
                };
            }
        }
        return null;
    }

    public async Task RevokeSessionsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

        var sessions = await tokenDb.Sessions.Where(s => s.UserId == userId).ToListAsync(ct);
        if (sessions.Count > 0)
        {
            foreach (var s in sessions)
            {
                _lastActivityUpdates.TryRemove(s.Token, out _);
            }
            tokenDb.Sessions.RemoveRange(sessions);
            await tokenDb.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Prunes expired token sessions from tokens.db. Called periodically by background services.
    /// </summary>
    public async Task<int> PruneExpiredTokensAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tokenDb = scope.ServiceProvider.GetRequiredService<TokenDbContext>();

            var now = DateTime.UtcNow;
            var expiredSessions = await tokenDb.Sessions
                .Where(s => s.ExpiresAt < now)
                .ToListAsync(ct);

            if (expiredSessions.Count > 0)
            {
                foreach (var s in expiredSessions)
                {
                    _lastActivityUpdates.TryRemove(s.Token, out _);
                }
                tokenDb.Sessions.RemoveRange(expiredSessions);
                await tokenDb.SaveChangesAsync(ct);
                _logger.LogInformation("Pruned {Count} expired token session(s) from tokens database.", expiredSessions.Count);
            }

            // Also sweep any stale entries in _lastActivityUpdates older than 10 minutes
            var staleThreshold = now.AddMinutes(-10);
            foreach (var kvp in _lastActivityUpdates)
            {
                if (kvp.Value < staleThreshold)
                {
                    _lastActivityUpdates.TryRemove(kvp.Key, out _);
                }
            }

            return expiredSessions.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to prune expired token sessions from tokens database.");
            return 0;
        }
    }
}
