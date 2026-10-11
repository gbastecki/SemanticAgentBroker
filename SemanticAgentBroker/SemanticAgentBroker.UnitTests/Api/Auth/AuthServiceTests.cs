using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SemanticAgentBroker.Api.Features.Auth;
using SemanticAgentBroker.Shared.Domain;
using SemanticAgentBroker.UnitTests.TestInfrastructure;

namespace SemanticAgentBroker.UnitTests.Api.Auth;

[TestClass]
public class AuthServiceTests
{
    private TestDatabaseFixture _dbFixture = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private AuthService _authService = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbFixture = new TestDatabaseFixture();

        var services = new ServiceCollection();
        services.AddScoped(_ => _dbFixture.CreateAuthDbContext());
        services.AddScoped(_ => _dbFixture.CreateTokenDbContext());
        var sp = services.BuildServiceProvider();
        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(_dbFixture.CreateSystemOptions());
        _authService = new AuthService(_scopeFactory, options, NullLogger<AuthService>.Instance);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _dbFixture.Dispose();
    }

    [TestMethod]
    public async Task LoginAsync_WithValidCredentials_ReturnsSessionToken()
    {
        // Arrange - seed user with hashed password
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "testuser",
                Role = "User"
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "SecurePass123!");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // Act
        var token = await _authService.LoginAsync("testuser", "SecurePass123!", "127.0.0.1", "browser-1");

        // Assert
        Assert.IsNotNull(token);
        Assert.IsTrue(token.Length > 20);

        // Verify token validation
        var session = await _authService.ValidateTokenAsync(token, "127.0.0.1", "browser-1");
        Assert.IsNotNull(session);
        Assert.AreEqual("testuser", session.Username);
        Assert.AreEqual("User", session.Role);
    }

    [TestMethod]
    public async Task LoginAsync_WithInvalidPassword_ThrowsUnauthorizedAndTracksFailedAttempts()
    {
        // Arrange - seed user
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User
            {
                Id = Guid.NewGuid(),
                Username = "testuser",
                Role = "User"
            };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "CorrectPassword");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _authService.LoginAsync("testuser", "WrongPassword", "192.168.1.100", "browser-1"));

        // Verify failure recorded
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var fail = authDb.FailedLoginAttempts.FirstOrDefault(f => f.IPAddress == "192.168.1.100");
            Assert.IsNotNull(fail);
            Assert.AreEqual(1, fail.Attempts);
        }
    }

    [TestMethod]
    public async Task ValidateTokenAsync_WithMismatchedBrowser_ReturnsNull()
    {
        // Arrange - Login
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "user1", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "pass1");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        var token = await _authService.LoginAsync("user1", "pass1", "127.0.0.1", "browser-1");
        Assert.IsNotNull(token);

        // Act - Attempt validation from different browser
        var session = await _authService.ValidateTokenAsync(token!, "127.0.0.1", "different-browser");

        // Assert
        Assert.IsNull(session);
    }

    [TestMethod]
    public async Task ValidateTokenAsync_WithLoopbackIpv4AndIpv6_MatchesAndValidatesSuccessfully()
    {
        // Arrange - Login from IPv4 127.0.0.1
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "loopbackuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "pass1");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        var token = await _authService.LoginAsync("loopbackuser", "pass1", "127.0.0.1", "browser-lb");
        Assert.IsNotNull(token);

        // Act - Validate from IPv6 loopback ::1
        var session = await _authService.ValidateTokenAsync(token!, "::1", "browser-lb");

        // Assert
        Assert.IsNotNull(session);
        Assert.AreEqual("loopbackuser", session.Username);
    }

    [TestMethod]
    public async Task ValidateTokenAsync_WithExpiredToken_RemovesSessionAndReturnsNull()
    {
        // Arrange - seed an expired token in TokenDbContext (using its hash as stored in DB)
        var expiredToken = "expired-" + Guid.NewGuid().ToString("N");
        var expiredTokenHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expiredToken)));
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = expiredTokenHash,
                UserId = Guid.NewGuid(),
                Username = "expireduser",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await tokenDb.SaveChangesAsync();
        }

        // Act
        var session = await _authService.ValidateTokenAsync(expiredToken, "127.0.0.1", "b1");

        // Assert
        Assert.IsNull(session);
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            Assert.IsNull(await tokenDb.Sessions.FindAsync(expiredTokenHash));
        }
    }

    [TestMethod]
    public async Task ValidateTokenAsync_ConcurrentExpiredTokenValidation_ReturnsNullWithoutException()
    {
        // Arrange
        var expiredToken = "expired-concurrent-" + Guid.NewGuid().ToString("N");
        var expiredTokenHash = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(expiredToken)));
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = expiredTokenHash,
                UserId = Guid.NewGuid(),
                Username = "expireduser",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                CreatedAt = DateTime.UtcNow.AddHours(-2),
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5)
            });
            await tokenDb.SaveChangesAsync();
        }

        // Act
        var tasks = Enumerable.Range(0, 10)
            .Select(_ => _authService.ValidateTokenAsync(expiredToken, "127.0.0.1", "b1"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        // Assert
        foreach (var res in results)
        {
            Assert.IsNull(res);
        }
    }

    [TestMethod]
    public async Task RevokeSessionsForUserAsync_RemovesAllUserSessionsOnly()
    {
        // Arrange
        var targetUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var token1 = "tok-1-" + Guid.NewGuid().ToString("N");
        var token2 = "tok-2-" + Guid.NewGuid().ToString("N");
        var otherToken = "tok-other-" + Guid.NewGuid().ToString("N");

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = token1,
                UserId = targetUserId,
                Username = "target",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = token2,
                UserId = targetUserId,
                Username = "target",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b2",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = otherToken,
                UserId = otherUserId,
                Username = "other",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b3",
                ExpiresAt = DateTime.UtcNow.AddHours(1)
            });
            await tokenDb.SaveChangesAsync();
        }

        // Act
        await _authService.RevokeSessionsForUserAsync(targetUserId);

        // Assert
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            Assert.IsNull(await tokenDb.Sessions.FindAsync(token1));
            Assert.IsNull(await tokenDb.Sessions.FindAsync(token2));
            Assert.IsNotNull(await tokenDb.Sessions.FindAsync(otherToken));
        }
    }

    [TestMethod]
    public async Task PruneExpiredTokensAsync_PrunesExpiredAndKeepsActive()
    {
        // Arrange
        var exp1 = "exp-1-" + Guid.NewGuid().ToString("N");
        var exp2 = "exp-2-" + Guid.NewGuid().ToString("N");
        var act1 = "act-1-" + Guid.NewGuid().ToString("N");

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = exp1,
                UserId = Guid.NewGuid(),
                Username = "u1",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-10)
            });
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = exp2,
                UserId = Guid.NewGuid(),
                Username = "u2",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b2",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-20)
            });
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = act1,
                UserId = Guid.NewGuid(),
                Username = "u3",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b3",
                ExpiresAt = DateTime.UtcNow.AddHours(2)
            });
            await tokenDb.SaveChangesAsync();
        }

        // Act
        int prunedCount = await _authService.PruneExpiredTokensAsync();

        // Assert
        Assert.AreEqual(2, prunedCount);
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            Assert.IsNull(await tokenDb.Sessions.FindAsync(exp1));
            Assert.IsNull(await tokenDb.Sessions.FindAsync(exp2));
            Assert.IsNotNull(await tokenDb.Sessions.FindAsync(act1));
        }
    }

    [TestMethod]
    public async Task LoginAsync_WhenLockoutExpired_ResetsAttemptsAndPermitsLogin()
    {
        // Arrange - Seed user and an expired FailedLoginAttempt
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "resetuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "validpass");
            authDb.Users.Add(user);

            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = "192.168.1.55",
                Attempts = 5,
                LockoutEnd = DateTime.UtcNow.AddMinutes(-5) // Expired lockout
            });
            await authDb.SaveChangesAsync();
        }

        // Act
        var token = await _authService.LoginAsync("resetuser", "validpass", "192.168.1.55", "browser-1");

        // Assert - login succeeds and fails record is cleared
        Assert.IsNotNull(token);
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var fail = await authDb.FailedLoginAttempts.FirstOrDefaultAsync(f => f.IPAddress == "192.168.1.55");
            Assert.IsNull(fail, "Successful login should clear the failed attempts record.");
        }
    }

    [TestMethod]
    public async Task LoginAsync_WithPriorFailedAttemptsBelowLockout_SuccessfulLoginClearsFailedRecord()
    {
        // Arrange - Seed user and 2 failed attempts (below threshold of 3, LockoutEnd is null)
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "partialfailuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "validpass");
            authDb.Users.Add(user);

            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = "192.168.1.77",
                Attempts = 2,
                LockoutEnd = null
            });
            await authDb.SaveChangesAsync();
        }

        // Act
        var token = await _authService.LoginAsync("partialfailuser", "validpass", "192.168.1.77", "browser-1");

        // Assert - Login succeeds and failed attempts record is completely removed from DB
        Assert.IsNotNull(token);
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var fail = await authDb.FailedLoginAttempts.FirstOrDefaultAsync(f => f.IPAddress == "192.168.1.77");
            Assert.IsNull(fail, "Successful login must remove pending failed login records from the database.");
        }
    }

    [TestMethod]
    public async Task LoginAsync_SuccessfulLogin_ClearsFailedAttemptsFromAllIPsForUsername()
    {
        // Arrange - Seed user and failed attempts from two different IPs
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "multiipuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "validpass");
            authDb.Users.Add(user);

            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = "10.0.0.1",
                Username = "multiipuser",
                Attempts = 1
            });
            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = "10.0.0.2",
                Username = "multiipuser",
                Attempts = 1
            });
            await authDb.SaveChangesAsync();
        }

        // Act - User logs in with valid credentials from a third IP (10.0.0.3)
        var token = await _authService.LoginAsync("multiipuser", "validpass", "10.0.0.3", "browser-1");

        // Assert - Login succeeds and ALL failed attempts for multiipuser across all IPs are cleared
        Assert.IsNotNull(token);
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var remainingFails = await authDb.FailedLoginAttempts
                .Where(f => f.Username == "multiipuser")
                .ToListAsync();
            Assert.AreEqual(0, remainingFails.Count, "Successful login must clear all failed attempts for the username across all IP addresses.");
        }
    }

    [TestMethod]
    public async Task Concurrency_ConcurrentFailedLogins_FromMultipleIPs_EnforcesAggregateLockout()
    {
        // Arrange - Seed user
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "distuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "validpass");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // Act - 10 concurrent requests from 10 distinct IPs with wrong password
        var tasks = Enumerable.Range(1, 10).Select(async i =>
        {
            try
            {
                await _authService.LoginAsync("distuser", "WrongPassword", $"10.1.0.{i}", $"browser-{i}");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        });

        var results = await Task.WhenAll(tasks);
        Assert.IsTrue(results.All(r => r));

        // After aggregate threshold (3) is exceeded, subsequent login attempt must be locked out
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
        {
            await _authService.LoginAsync("distuser", "validpass", "10.1.0.99", "browser-99");
        });

        Assert.IsTrue(ex.Message.Contains("Too many failed attempts"), $"Expected lockout message but got: {ex.Message}");
    }

    [TestMethod]
    public async Task LogoutByTokenAsync_RemovesSessionFromDatabase()
    {
        // Arrange - Login
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "user1", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "pass1");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        var token = await _authService.LoginAsync("user1", "pass1", "127.0.0.1", "browser-1");

        // Act
        bool loggedOut = await _authService.LogoutByTokenAsync(token!, "127.0.0.1", "browser-1");

        // Assert
        Assert.IsTrue(loggedOut);
        var session = await _authService.ValidateTokenAsync(token!, "127.0.0.1", "browser-1");
        Assert.IsNull(session);
    }

    [TestMethod]
    public async Task Concurrency_ConcurrentFailedLogins_EnforcesLockoutWithoutRaceCondition()
    {
        // Arrange - seed user
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "bruteuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "CorrectPassword");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        const string targetIp = "10.0.0.99";
        const int concurrentAttempts = 10;

        // Act - trigger concurrent login attempts with wrong passwords
        var tasks = Enumerable.Range(0, concurrentAttempts).Select(async _ =>
        {
            try
            {
                await _authService.LoginAsync("bruteuser", "WrongPassword", targetIp, "browser-x");
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return true; // expected
            }
        });

        var results = await Task.WhenAll(tasks);

        // Assert - all should fail with UnauthorizedAccessException
        Assert.IsTrue(results.All(r => r));

        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var fails = authDb.FailedLoginAttempts.Where(f => f.IPAddress == targetIp).ToList();
            Assert.AreEqual(1, fails.Count, "Should have exactly one FailedLoginAttempt row for the IP");
            Assert.IsTrue(fails[0].Attempts >= 3, "Attempts should have reached lockout threshold");
            Assert.IsNotNull(fails[0].LockoutEnd, "LockoutEnd should be set");
        }
    }

    [TestMethod]
    public async Task LoginAsync_TokenIsHashedInDatabase_NotStoredPlaintext()
    {
        // Arrange
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "hashuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "pass1");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // Act
        var token = await _authService.LoginAsync("hashuser", "pass1", "127.0.0.1", "browser-1");

        // Assert - token returned to user must NOT exist as plaintext in DB
        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            var plainMatch = await tokenDb.Sessions.FirstOrDefaultAsync(s => s.Token == token);
            Assert.IsNull(plainMatch, "Token must NOT be stored in plaintext in the database.");

            // The stored token should be a SHA-256 hash
            var hash = Convert.ToBase64String(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(token!)));
            var hashMatch = await tokenDb.Sessions.FirstOrDefaultAsync(s => s.Token == hash);
            Assert.IsNotNull(hashMatch, "Token must be stored as SHA-256 hash in the database.");
        }

        // Validate still works
        var session = await _authService.ValidateTokenAsync(token!, "127.0.0.1", "browser-1");
        Assert.IsNotNull(session);
    }

    [TestMethod]
    public async Task LoginAsync_RotatingIPs_StillLocksOutByUsername()
    {
        // Arrange
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "targetuser", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "RealPassword");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        // Act - brute-force from different IPs (simulating rotating proxies/botnet)
        // MaxFailedAttempts is 3 in test fixture
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await _authService.LoginAsync("targetuser", "WrongPassword", $"10.0.0.{i}", "browser-x");
            }
            catch (UnauthorizedAccessException) { }
        }

        // Assert - the 4th attempt from yet another IP should be locked out
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _authService.LoginAsync("targetuser", "WrongPassword", "10.0.0.99", "browser-x"));
        StringAssert.Contains(ex.Message, "Too many failed attempts");
    }

    [TestMethod]
    public async Task LoginAsync_WhenConcurrentMultiIpFailsLeaveNoLockoutEnd_SetsLockoutEndAndPermitsLoginAfterExpiry()
    {
        // Arrange - User exists
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "race_lockout_user", Role = "User" };
            user.PasswordHash = new PasswordHasher<User>().HashPassword(user, "RealPassword123!");
            authDb.Users.Add(user);

            // Simulate race condition where 3 concurrent requests from different IPs (each with its own IP lock)
            // read initial userAttempts=0 and wrote Attempts=1 with LockoutEnd=null
            authDb.FailedLoginAttempts.AddRange(
                new FailedLoginAttempt { IPAddress = "10.0.1.1", Username = "race_lockout_user", Attempts = 1, LockoutEnd = null },
                new FailedLoginAttempt { IPAddress = "10.0.1.2", Username = "race_lockout_user", Attempts = 1, LockoutEnd = null },
                new FailedLoginAttempt { IPAddress = "10.0.1.3", Username = "race_lockout_user", Attempts = 1, LockoutEnd = null }
            );
            await authDb.SaveChangesAsync();
        }

        // Act 1: Subsequent login attempt is blocked because total attempts = 3 >= MaxFailedAttempts (3)
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _authService.LoginAsync("race_lockout_user", "RealPassword123!", "10.0.1.4", "browser-x"));
        StringAssert.Contains(ex.Message, "Too many failed attempts");

        // Assert 1: The system MUST have set a LockoutEnd on at least one record, otherwise it is permanently locked out
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var fails = await authDb.FailedLoginAttempts.Where(f => f.Username == "race_lockout_user").ToListAsync();
            Assert.IsTrue(fails.Any(f => f.LockoutEnd.HasValue), "LockoutEnd MUST be set when aggregate attempts >= MaxFailedAttempts so lockout is not permanent.");

            // Fast-forward lockout expiration
            foreach (var f in fails.Where(f => f.LockoutEnd.HasValue))
            {
                f.LockoutEnd = DateTime.UtcNow.AddMinutes(-1);
            }
            await authDb.SaveChangesAsync();
        }

        // Act 2: After expiry, login with valid credentials must succeed
        var token = await _authService.LoginAsync("race_lockout_user", "RealPassword123!", "10.0.1.4", "browser-x");

        // Assert 2: Successfully logged in
        Assert.IsNotNull(token);
    }

    [TestMethod]
    public async Task LoginSuccess_ClearsOnlyMatchingUsernameAndIpAttempts_DoesNotClearOtherUsersAttemptsFromSameIp()
    {
        // Arrange
        var ip = "192.168.1.50";
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var hasher = new PasswordHasher<User>();

            var victim = new User { Id = Guid.NewGuid(), Username = "victim", Role = "User" };
            victim.PasswordHash = hasher.HashPassword(victim, "VictimPass123!");

            var attacker = new User { Id = Guid.NewGuid(), Username = "attacker", Role = "User" };
            attacker.PasswordHash = hasher.HashPassword(attacker, "AttackerPass123!");

            authDb.Users.AddRange(victim, attacker);

            // Seed failed attempt for victim from the shared IP
            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = ip,
                Username = "victim",
                Attempts = 2
            });

            await authDb.SaveChangesAsync();
        }

        // Act - Attacker logs in successfully from the same IP
        var token = await _authService.LoginAsync("attacker", "AttackerPass123!", ip, "browser-attacker");
        Assert.IsNotNull(token);

        // Assert - Victim's failed login attempt from this IP must NOT be cleared
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var victimFails = await authDb.FailedLoginAttempts
                .FirstOrDefaultAsync(f => f.Username == "victim" && f.IPAddress == ip);

            Assert.IsNotNull(victimFails, "Victim's failed login attempts must not be cleared by attacker's successful login from the same IP.");
            Assert.AreEqual(2, victimFails.Attempts);
        }
    }

    [TestMethod]
    public async Task LoginAsync_AfterLockoutExpires_SingleIp_DoesNotImmediatelyRelockUserOnFirstFailedAttempt()
    {
        // Arrange
        const string username = "relock_test_user";
        const string ip = "192.168.1.100";

        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var hasher = new PasswordHasher<User>();
            var user = new User { Id = Guid.NewGuid(), Username = username, Role = "User" };
            user.PasswordHash = hasher.HashPassword(user, "CorrectPassword123!");
            authDb.Users.Add(user);

            // Seed locked out record where lockout expired in the past
            authDb.FailedLoginAttempts.Add(new FailedLoginAttempt
            {
                IPAddress = ip,
                Username = username,
                Attempts = 3,
                LockoutEnd = DateTime.UtcNow.AddMinutes(-5)
            });

            await authDb.SaveChangesAsync();
        }

        // Act - User mistypes their password ONCE after waiting out the lockout
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _authService.LoginAsync(username, "WrongPasswordOnce!", ip, "browser-1"));

        Assert.AreEqual("Invalid credentials.", ex.Message);

        // Assert - The user must NOT be immediately re-locked out on attempt #1
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var failRecord = await authDb.FailedLoginAttempts
                .FirstOrDefaultAsync(f => f.Username == username && f.IPAddress == ip);

            Assert.IsNotNull(failRecord);
            Assert.AreEqual(1, failRecord.Attempts, "Attempts must be 1 on first failure after lockout expiry.");
            Assert.IsNull(failRecord.LockoutEnd, "User must NOT be locked out after only 1 failed attempt following lockout expiry.");
        }
    }

    [TestMethod]
    public async Task LoginAsync_AfterLockoutExpires_MultiIp_DoesNotImmediatelyRelockUserOnFirstFailedAttempt()
    {
        // Arrange
        const string username = "relock_multi_user";

        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var hasher = new PasswordHasher<User>();
            var user = new User { Id = Guid.NewGuid(), Username = username, Role = "User" };
            user.PasswordHash = hasher.HashPassword(user, "CorrectPassword123!");
            authDb.Users.Add(user);

            // Seed multi-IP aggregate lockout that expired in the past
            authDb.FailedLoginAttempts.AddRange(
                new FailedLoginAttempt { IPAddress = "10.0.2.1", Username = username, Attempts = 1, LockoutEnd = DateTime.UtcNow.AddMinutes(-10) },
                new FailedLoginAttempt { IPAddress = "10.0.2.2", Username = username, Attempts = 2, LockoutEnd = DateTime.UtcNow.AddMinutes(-10) }
            );

            await authDb.SaveChangesAsync();
        }

        // Act - User attempts login from a new IP with wrong password after lockout expired
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _authService.LoginAsync(username, "WrongPasswordOnce!", "10.0.2.3", "browser-2"));

        Assert.AreEqual("Invalid credentials.", ex.Message);

        // Assert - User must NOT be locked out
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var failRecord = await authDb.FailedLoginAttempts
                .FirstOrDefaultAsync(f => f.Username == username && f.IPAddress == "10.0.2.3");

            Assert.IsNotNull(failRecord);
            Assert.AreEqual(1, failRecord.Attempts, "Attempts must be 1 on first failure after multi-IP lockout expiry.");
            Assert.IsNull(failRecord.LockoutEnd, "User must NOT be locked out after only 1 failed attempt following multi-IP lockout expiry.");
        }
    }

    [TestMethod]
    public async Task LogoutByTokenAsync_RemovesTokenFromActivityCache()
    {
        // Arrange
        using (var authDb = _dbFixture.CreateAuthDbContext())
        {
            var user = new User { Id = Guid.NewGuid(), Username = "logout_user", Role = "User" };
            var hasher = new PasswordHasher<User>();
            user.PasswordHash = hasher.HashPassword(user, "Pass123!");
            authDb.Users.Add(user);
            await authDb.SaveChangesAsync();
        }

        var token = await _authService.LoginAsync("logout_user", "Pass123!", "127.0.0.1", "browser-logout");
        Assert.IsNotNull(token);

        // Validate token to populate activity cache
        await _authService.ValidateTokenAsync(token, "127.0.0.1", "browser-logout");

        var field = typeof(AuthService).GetField("_lastActivityUpdates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)field!.GetValue(null)!;
        
        var tokenHash = AuthService.HashToken(token);
        // Force an entry into dict
        dict[tokenHash] = DateTime.UtcNow;
        Assert.IsTrue(dict.ContainsKey(tokenHash));

        // Act
        var result = await _authService.LogoutByTokenAsync(token, "127.0.0.1", "browser-logout");

        // Assert
        Assert.IsTrue(result);
        Assert.IsFalse(dict.ContainsKey(tokenHash), "Token hash must be removed from activity cache upon logout");
    }

    [TestMethod]
    public async Task PruneExpiredTokensAsync_RemovesExpiredTokensFromActivityCache()
    {
        // Arrange - seed an expired session in tokenDb
        var token = "expired-token-xyz-1234567890";
        var tokenHash = AuthService.HashToken(token);

        using (var tokenDb = _dbFixture.CreateTokenDbContext())
        {
            tokenDb.Sessions.Add(new UserSessionRecord
            {
                Token = tokenHash,
                UserId = Guid.NewGuid(),
                Username = "expired_user",
                Role = "User",
                IPAddress = "127.0.0.1",
                BrowserId = "b1",
                ExpiresAt = DateTime.UtcNow.AddMinutes(-5),
                LastActivityAt = DateTime.UtcNow.AddMinutes(-10)
            });
            await tokenDb.SaveChangesAsync();
        }

        var field = typeof(AuthService).GetField("_lastActivityUpdates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        var dict = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)field!.GetValue(null)!;
        dict[tokenHash] = DateTime.UtcNow.AddMinutes(-10);

        // Act
        var pruned = await _authService.PruneExpiredTokensAsync();

        // Assert
        Assert.IsTrue(pruned > 0);
        Assert.IsFalse(dict.ContainsKey(tokenHash), "Expired token must be cleaned up from activity cache");
    }
}


