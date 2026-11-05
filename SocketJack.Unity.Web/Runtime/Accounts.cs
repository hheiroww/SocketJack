using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SocketJack.Multiplayer.Web
{
    public sealed class StoredAccount
    {
        public WebAccount Account { get; set; }
        public byte[] Salt { get; set; }
        public byte[] PasswordHash { get; set; }
        public int Iterations { get; set; }
        public bool IsBanned { get; set; }
    }
    /// <summary>Operator-owned durable account store. Session keys are hashes, never reusable bearer tokens.</summary>
    public interface IAccountStore
    {
        StoredAccount FindByUsername(string normalizedUsername);
        StoredAccount FindById(string id);
        bool HasAdministrator { get; }
        bool TryCreate(StoredAccount account, bool requireFirstAdministrator = false);
        void PutSession(string tokenHash, string accountId, DateTime expiresUtc);
        StoredAccount FindSession(string tokenHash, DateTime utcNow);
        void RevokeSession(string tokenHash);
        void RevokeAccountSessions(string accountId);
        void SetBanned(string accountId, bool banned);
        void ReplacePassword(string accountId, byte[] salt, byte[] passwordHash, int iterations);
    }
    public interface IAccountProvider
    {
        Task<WebAccountSession> RegisterAsync(string username, string password, string displayName, CancellationToken token = default);
        Task<WebAccountSession> SignInAsync(string username, string password, CancellationToken token = default);
        WebAccount Resume(string token);
        void SignOut(string token);
        WebAccount FindById(string id);
        WebAccount FindByUsername(string username);
    }

    /// <summary>PBKDF2 account provider using the platform cryptography implementation. Password work is bounded.</summary>
    public sealed class LocalAccountProvider : IAccountProvider, IDisposable
    {
        public const int PasswordIterations = 600000;
        private readonly IAccountStore store;
        private readonly SemaphoreSlim hashing = new SemaphoreSlim(2, 2);
        private readonly object bootstrapLock = new object();
        private readonly byte[] bootstrapHash;
        private readonly Func<DateTime> utcNow;
        private bool disposed;
        public string BootstrapCode { get; }
        public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(12);

        public LocalAccountProvider(IAccountStore store, Func<DateTime> utcNow = null)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store)); this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (!store.HasAdministrator) { BootstrapCode = RandomToken(); bootstrapHash = HashToken(BootstrapCode); }
        }
        public Task<WebAccountSession> RegisterAsync(string username, string password, string displayName, CancellationToken token = default)
            => CreateAsync(username, password, displayName, false, token);
        public async Task<WebAccountSession> BootstrapAsync(string code, string username, string password, string displayName, CancellationToken token = default)
        {
            if (bootstrapHash == null || string.IsNullOrEmpty(code) || code.Length > 256 || !CryptographicOperations.FixedTimeEquals(HashToken(code), bootstrapHash))
                throw new WebAccountException("The setup code is invalid or no longer available.", 403);
            return await CreateAsync(username, password, displayName, true, token).ConfigureAwait(false);
        }
        private async Task<WebAccountSession> CreateAsync(string username, string password, string displayName, bool administrator, CancellationToken token)
        {
            ThrowIfDisposed(); string normalized = NormalizeUsername(username); ValidatePassword(password);
            displayName = ValidateDisplayName(string.IsNullOrWhiteSpace(displayName) ? username : displayName);
            if (!await hashing.WaitAsync(0, token).ConfigureAwait(false)) throw new WebAccountException("Account verification is busy. Try again shortly.", 429);
            try
            {
                byte[] salt = RandomBytes(16);
                byte[] hash = await Task.Run(() => Derive(password, salt, PasswordIterations), token).ConfigureAwait(false);
                var account = new StoredAccount
                {
                    Account = new WebAccount { Id = Guid.NewGuid().ToString("N"), Username = normalized, DisplayName = displayName, Role = administrator ? "admin" : "player" },
                    Salt = salt, PasswordHash = hash, Iterations = PasswordIterations
                };
                lock (bootstrapLock)
                {
                    if (!store.TryCreate(account, administrator)) throw new WebAccountException(administrator ? "This server already has an operator account." : "That username is unavailable.", 409);
                }
                return IssueSession(account.Account);
            }
            finally { hashing.Release(); }
        }
        public async Task<WebAccountSession> SignInAsync(string username, string password, CancellationToken token = default)
        {
            ThrowIfDisposed(); string normalized;
            try { normalized = NormalizeUsername(username); ValidatePasswordLength(password); }
            catch (WebAccountException) { throw new WebAccountException("Username or password is incorrect.", 401); }
            if (!await hashing.WaitAsync(0, token).ConfigureAwait(false)) throw new WebAccountException("Account verification is busy. Try again shortly.", 429);
            try
            {
                var stored = store.FindByUsername(normalized);
                // Equal password work for unknown accounts reduces trivial account enumeration timing.
                byte[] salt = stored?.Salt ?? new byte[16]; int iterations = stored?.Iterations ?? PasswordIterations;
                var actual = await Task.Run(() => Derive(password, salt, iterations), token).ConfigureAwait(false);
                if (stored == null || stored.IsBanned || !CryptographicOperations.FixedTimeEquals(actual, stored.PasswordHash))
                    throw new WebAccountException("Username or password is incorrect.", 401);
                return IssueSession(stored.Account);
            }
            finally { hashing.Release(); }
        }
        public WebAccount Resume(string token)
        {
            ThrowIfDisposed(); if (string.IsNullOrEmpty(token) || token.Length > 256) return null;
            var stored = store.FindSession(HashTokenText(token), utcNow()); return stored == null || stored.IsBanned ? null : Copy(stored.Account);
        }
        public void SignOut(string token) { if (!string.IsNullOrEmpty(token) && token.Length <= 256) store.RevokeSession(HashTokenText(token)); }
        public WebAccount FindById(string id) { var record = store.FindById(id); return record == null || record.IsBanned ? null : Copy(record.Account); }
        public WebAccount FindByUsername(string username)
        {
            string normalized; try { normalized = NormalizeUsername(username); } catch (WebAccountException) { return null; }
            var record = store.FindByUsername(normalized); return record == null || record.IsBanned ? null : Copy(record.Account);
        }
        public async Task ResetPasswordAsync(string accountId, string password, CancellationToken token = default)
        {
            ValidatePassword(password); if (store.FindById(accountId) == null) throw new WebAccountException("Account was not found.", 404);
            if (!await hashing.WaitAsync(0, token).ConfigureAwait(false)) throw new WebAccountException("Account verification is busy. Try again shortly.", 429);
            try { byte[] salt = RandomBytes(16); byte[] hash = await Task.Run(() => Derive(password, salt, PasswordIterations), token).ConfigureAwait(false); store.ReplacePassword(accountId, salt, hash, PasswordIterations); store.RevokeAccountSessions(accountId); }
            finally { hashing.Release(); }
        }
        private WebAccountSession IssueSession(WebAccount account)
        {
            if (SessionLifetime <= TimeSpan.Zero || SessionLifetime > TimeSpan.FromDays(7)) throw new InvalidOperationException("Session lifetime must be positive and at most seven days.");
            string token = RandomToken(); store.PutSession(HashTokenText(token), account.Id, utcNow().Add(SessionLifetime));
            return new WebAccountSession { Token = token, User = Copy(account) };
        }
        public static string NormalizeUsername(string username)
        {
            if (username == null || !Regex.IsMatch(username.Trim(), "^[A-Za-z][A-Za-z0-9_]{2,31}$")) throw new WebAccountException("Use a username of 3–32 letters, numbers or underscores, starting with a letter.");
            return username.Trim().ToLowerInvariant();
        }
        private static string ValidateDisplayName(string name)
        {
            name = name?.Trim(); if (string.IsNullOrEmpty(name) || name.Length > 48) throw new WebAccountException("Display name must contain 1–48 characters.");
            foreach (char value in name) if (char.IsControl(value)) throw new WebAccountException("Display name cannot contain control characters.");
            return name;
        }
        private static void ValidatePasswordLength(string password) { if (password == null || password.Length > 128 || Encoding.UTF8.GetByteCount(password) > 512) throw new WebAccountException("Password must contain 12–128 characters."); }
        private static void ValidatePassword(string password) { ValidatePasswordLength(password); if (password.Length < 12) throw new WebAccountException("Password must contain 12–128 characters."); }
        private static byte[] Derive(string password, byte[] salt, int iterations)
        {
            if (iterations < 100000 || iterations > 2000000 || salt == null || salt.Length < 16 || salt.Length > 64) throw new InvalidOperationException("Stored password parameters are outside their supported bounds.");
            using (var pbkdf = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256)) return pbkdf.GetBytes(32);
        }
        internal static WebAccount Copy(WebAccount account) => new WebAccount { Id = account.Id, Username = account.Username, DisplayName = account.DisplayName, Role = account.Role };
        internal static byte[] RandomBytes(int count) { var bytes = new byte[count]; using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes); return bytes; }
        internal static string RandomToken() => Convert.ToBase64String(RandomBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        internal static byte[] HashToken(string token) { using (var sha = SHA256.Create()) return sha.ComputeHash(Encoding.UTF8.GetBytes(token)); }
        internal static string HashTokenText(string token) => BitConverter.ToString(HashToken(token)).Replace("-", "").ToLowerInvariant();
        private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(LocalAccountProvider)); }
        public void Dispose() { disposed = true; hashing.Dispose(); }
    }
}
