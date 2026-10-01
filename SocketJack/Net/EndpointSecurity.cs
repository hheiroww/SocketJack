using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace SocketJack.Net {

    public enum TuningProfile {
        Loose,
        Firm,
        Strict
    }

    public enum BotTuningProfile {
        Loose,
        Firm,
        Strict
    }

    /// <summary>
    /// Tunable bot filtering and endpoint abuse controls for HTTP, SocketJack,
    /// WebSocket, and filesystem-backed request surfaces.
    /// </summary>
    public sealed class EndpointSecurityOptions {
        private TuningProfile _tuningProfile = TuningProfile.Firm;
        private BotTuningProfile _botTuningProfile = BotTuningProfile.Firm;

        internal EndpointSecurityOptions Copy() => (EndpointSecurityOptions)MemberwiseClone();

        public EndpointSecurityOptions() {
            ApplyTuningProfile(_tuningProfile);
            ApplyBotTuningProfile(_botTuningProfile);
        }

        public TuningProfile TuningProfile {
            get { return _tuningProfile; }
            set {
                _tuningProfile = value;
                ApplyTuningProfile(value);
            }
        }

        public BotTuningProfile BotTuningProfile {
            get { return _botTuningProfile; }
            set {
                _botTuningProfile = value;
                ApplyBotTuningProfile(value);
            }
        }

        public bool Enabled { get; set; } = true;
        public bool ExemptLoopback { get; set; } = true;
        public bool TrustForwardedForHeaders { get; set; } = false;
        public int MaxTrackedClients { get; set; } = 4096;
        public int GeneralRequestsPerMinute { get; set; }
        public int AdministrativeRequestsPerMinute { get; set; }
        public int SocketJackFramesPerMinute { get; set; }
        public int MinimumEnforcedThrottleMs { get; set; }
        public int MaxThrottleMs { get; set; }
        public double MinimumScoreForThrottle { get; set; }
        public double ThrottleMsPerScore { get; set; }
        public double ThrottleDecayMsPerSecond { get; set; }
        public double ScoreDecayPerSecond { get; set; }
        public double DisableScoreThreshold { get; set; }
        public int MinimumScoredEventsBeforeDisable { get; set; }
        public int MinDisableSeconds { get; set; }
        public int MaxDisableSeconds { get; set; }
        public double BurstReferenceSeconds { get; set; }
        public int BurstGraceEvents { get; set; }
        public double MaxFrequencyMultiplier { get; set; }
        public bool BlockKnownBadPaths { get; set; } = true;
        public bool TreatSuspiciousUserAgentsAsBad { get; set; }

        public void UseLooseFiltering() {
            TuningProfile = TuningProfile.Loose;
        }

        public void UseFirmFiltering() {
            TuningProfile = TuningProfile.Firm;
        }

        public void UseStrictFiltering() {
            TuningProfile = TuningProfile.Strict;
        }

        public void UseLooseBotFiltering() {
            BotTuningProfile = BotTuningProfile.Loose;
        }

        public void UseFirmBotFiltering() {
            BotTuningProfile = BotTuningProfile.Firm;
        }

        public void UseStrictBotFiltering() {
            BotTuningProfile = BotTuningProfile.Strict;
        }

        private void ApplyTuningProfile(TuningProfile profile) {
            switch (profile) {
                case TuningProfile.Loose:
                    AdministrativeRequestsPerMinute = 180;
                    MinimumEnforcedThrottleMs = 100;
                    MaxThrottleMs = 250;
                    MinimumScoreForThrottle = 80.0;
                    ThrottleMsPerScore = 0.75;
                    ThrottleDecayMsPerSecond = 500.0;
                    ScoreDecayPerSecond = 8.0;
                    DisableScoreThreshold = 1500.0;
                    MinimumScoredEventsBeforeDisable = 24;
                    MinDisableSeconds = 15;
                    MaxDisableSeconds = 600;
                    break;
                case TuningProfile.Strict:
                    AdministrativeRequestsPerMinute = 45;
                    MinimumEnforcedThrottleMs = 25;
                    MaxThrottleMs = 3000;
                    MinimumScoreForThrottle = 18.0;
                    ThrottleMsPerScore = 4.0;
                    ThrottleDecayMsPerSecond = 175.0;
                    ScoreDecayPerSecond = 2.5;
                    DisableScoreThreshold = 120.0;
                    MinimumScoredEventsBeforeDisable = 3;
                    MinDisableSeconds = 60;
                    MaxDisableSeconds = 3600;
                    break;
                default:
                    AdministrativeRequestsPerMinute = 240;
                    MinimumEnforcedThrottleMs = 100;
                    MaxThrottleMs = 500;
                    MinimumScoreForThrottle = 150.0;
                    ThrottleMsPerScore = 0.75;
                    ThrottleDecayMsPerSecond = 500.0;
                    ScoreDecayPerSecond = 10.0;
                    DisableScoreThreshold = 1000.0;
                    MinimumScoredEventsBeforeDisable = 12;
                    MinDisableSeconds = 10;
                    MaxDisableSeconds = 600;
                    break;
            }
        }

        private void ApplyBotTuningProfile(BotTuningProfile profile) {
            switch (profile) {
                case BotTuningProfile.Loose:
                    GeneralRequestsPerMinute = 600;
                    SocketJackFramesPerMinute = 7200;
                    BurstReferenceSeconds = 0.5;
                    BurstGraceEvents = 60;
                    MaxFrequencyMultiplier = 4.0;
                    BlockKnownBadPaths = true;
                    TreatSuspiciousUserAgentsAsBad = false;
                    break;
                case BotTuningProfile.Strict:
                    GeneralRequestsPerMinute = 120;
                    SocketJackFramesPerMinute = 1800;
                    BurstReferenceSeconds = 2.0;
                    BurstGraceEvents = 8;
                    MaxFrequencyMultiplier = 12.0;
                    BlockKnownBadPaths = true;
                    TreatSuspiciousUserAgentsAsBad = true;
                    break;
                default:
                    GeneralRequestsPerMinute = 900;
                    SocketJackFramesPerMinute = 12000;
                    BurstReferenceSeconds = 0.25;
                    BurstGraceEvents = 120;
                    MaxFrequencyMultiplier = 3.0;
                    BlockKnownBadPaths = true;
                    TreatSuspiciousUserAgentsAsBad = false;
                    break;
            }
        }
    }

    public sealed class EndpointSecurityDecision {
        public bool Allowed { get; internal set; } = true;
        public bool IsDisabled { get; internal set; }
        public bool IsAdministrative { get; internal set; }
        public bool IsSuspicious { get; internal set; }
        public int DelayMilliseconds { get; internal set; }
        public int StatusCode { get; internal set; } = 200;
        public string ReasonPhrase { get; internal set; } = "OK";
        public string Message { get; internal set; } = "";
        public string ClientIp { get; internal set; } = "";
        public string Category { get; internal set; } = "";
        public string Endpoint { get; internal set; } = "";
        public string Reason { get; internal set; } = "";
        public double Score { get; internal set; }
        public double ThrottleMilliseconds { get; internal set; }
        public int RetryAfterSeconds { get; internal set; }
        public DateTimeOffset? DisabledUntilUtc { get; internal set; }

        internal static EndpointSecurityDecision Allow(string clientIp = "") {
            return new EndpointSecurityDecision {
                Allowed = true,
                ClientIp = clientIp ?? ""
            };
        }
    }

    public sealed class EndpointSecuritySnapshot {
        public string ClientIp { get; set; } = "";
        public double Score { get; set; }
        public double ThrottleMilliseconds { get; set; }
        public bool Disabled { get; set; }
        public string DisabledUntilUtc { get; set; } = "";
        public string LastSeenUtc { get; set; } = "";
        public string LastCategory { get; set; } = "";
        public string LastEndpoint { get; set; } = "";
        public string LastReason { get; set; } = "";
        public int WindowCount { get; set; }
    }

    /// <summary>
    /// Per-IP abuse monitor with decaying throttle and automatic temporary disables.
    /// </summary>
    public sealed class EndpointSecurityMonitor {
        private readonly ConcurrentDictionary<string, EndpointSecurityClientState> _clients =
            new ConcurrentDictionary<string, EndpointSecurityClientState>(StringComparer.OrdinalIgnoreCase);

        public EndpointSecurityDecision EvaluateHttpRequest(NetworkConnection connection, HttpRequest request, EndpointSecurityOptions options) {
            options = NormalizeOptions(options);
            string clientIp = ResolveClientIp(connection, request, options);
            if (!ShouldTrackClient(clientIp, options))
                return EndpointSecurityDecision.Allow(clientIp);

            bool administrative;
            bool suspicious;
            string reason;
            double severity = ClassifyHttpRequest(request, options, out administrative, out suspicious, out reason);
            int rateLimit = administrative ? options.AdministrativeRequestsPerMinute : options.GeneralRequestsPerMinute;
            string endpoint = ((request?.Method ?? "HTTP") + " " + (request?.Path ?? "")).Trim();
            return ApplyEvent(clientIp, "http", endpoint, severity, rateLimit, administrative, suspicious, reason, options);
        }

        public EndpointSecurityDecision RecordHttpResult(NetworkConnection connection, HttpRequest request, int statusCode, EndpointSecurityOptions options) {
            options = NormalizeOptions(options);
            if (statusCode < 400)
                return EndpointSecurityDecision.Allow(ResolveClientIp(connection, request, options));

            string clientIp = ResolveClientIp(connection, request, options);
            if (!ShouldTrackClient(clientIp, options))
                return EndpointSecurityDecision.Allow(clientIp);

            bool administrative = IsAdministrativeHttpEndpoint(request?.Path ?? "");
            double severity;
            if (statusCode == 401 || statusCode == 403) {
                severity = administrative ? 1.5 : 0.5;
            } else if (statusCode == 404) {
                severity = administrative ? 1.0 : 0.25;
            } else if (statusCode == 429) {
                severity = 4.0;
            } else {
                severity = 0.5;
            }

            string reason = "HTTP " + statusCode.ToString();
            int rateLimit = administrative ? options.AdministrativeRequestsPerMinute : options.GeneralRequestsPerMinute;
            string endpoint = ((request?.Method ?? "HTTP") + " " + (request?.Path ?? "")).Trim();
            bool suspicious = statusCode == 429 || (administrative && statusCode == 403);
            return ApplyEvent(clientIp, "http-result", endpoint, severity, rateLimit, administrative, suspicious, reason, options);
        }

        public EndpointSecurityDecision RecordProtocolEvent(NetworkConnection connection, string protocol, int bytes, bool administrative, double baseSeverity, string reason, EndpointSecurityOptions options) {
            options = NormalizeOptions(options);
            string clientIp = ResolveClientIp(connection);
            if (!ShouldTrackClient(clientIp, options))
                return EndpointSecurityDecision.Allow(clientIp);

            int rateLimit = administrative ? options.AdministrativeRequestsPerMinute : options.SocketJackFramesPerMinute;
            double severity = Math.Max(0, baseSeverity);
            if (bytes > 1024 * 1024)
                severity += 4.0;
            string endpoint = string.IsNullOrWhiteSpace(protocol) ? "protocol" : protocol.Trim();
            return ApplyEvent(clientIp, "protocol", endpoint, severity, rateLimit, administrative, severity >= 8.0, reason, options);
        }

        public EndpointSecurityDecision RecordSocketJackFrame(NetworkConnection connection, string protocol, int payloadLength, EndpointSecurityOptions options) {
            return RecordProtocolEvent(connection, protocol, payloadLength, false, 0.25, "frame", options);
        }

        public EndpointSecurityDecision RecordFilesystemEvent(string clientIp, string path, string operation, string reason, EndpointSecurityOptions options) {
            options = NormalizeOptions(options);
            clientIp = NormalizeClientIp(clientIp);
            if (!ShouldTrackClient(clientIp, options))
                return EndpointSecurityDecision.Allow(clientIp);

            string fullReason = string.IsNullOrWhiteSpace(reason) ? "filesystem guard" : reason.Trim();
            double severity = IsSuspiciousFilesystemPath(path) ? 35.0 : 10.0;
            string endpoint = (string.IsNullOrWhiteSpace(operation) ? "filesystem" : operation.Trim()) + " " + (path ?? "");
            return ApplyEvent(clientIp, "filesystem", endpoint, severity, options.AdministrativeRequestsPerMinute, true, true, fullReason, options);
        }

        public void DisableRemoteIp(string clientIp, TimeSpan duration, string reason = null) {
            clientIp = NormalizeClientIp(clientIp);
            if (string.IsNullOrWhiteSpace(clientIp))
                return;

            var state = _clients.GetOrAdd(clientIp, _ => new EndpointSecurityClientState());
            lock (state.SyncRoot) {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                state.DisabledUntilUtc = now.Add(duration <= TimeSpan.Zero ? TimeSpan.FromMinutes(5) : duration);
                state.LastSeenUtc = now;
                state.LastReason = string.IsNullOrWhiteSpace(reason) ? "manual disable" : reason.Trim();
                state.Score = Math.Max(state.Score, 120.0);
                state.ThrottleMs = Math.Max(state.ThrottleMs, 1000.0);
            }
        }

        public bool EnableRemoteIp(string clientIp) {
            clientIp = NormalizeClientIp(clientIp);
            if (string.IsNullOrWhiteSpace(clientIp))
                return false;
            return _clients.TryRemove(clientIp, out _);
        }

        public IReadOnlyList<EndpointSecuritySnapshot> GetSnapshots(int limit = 100) {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var snapshots = new List<EndpointSecuritySnapshot>();
            foreach (var kv in _clients) {
                EndpointSecurityClientState state = kv.Value;
                lock (state.SyncRoot) {
                    snapshots.Add(new EndpointSecuritySnapshot {
                        ClientIp = kv.Key,
                        Score = Math.Round(state.Score, 2),
                        ThrottleMilliseconds = Math.Round(state.ThrottleMs, 2),
                        Disabled = state.DisabledUntilUtc > now,
                        DisabledUntilUtc = state.DisabledUntilUtc > now ? state.DisabledUntilUtc.ToString("O") : "",
                        LastSeenUtc = state.LastSeenUtc == default ? "" : state.LastSeenUtc.ToString("O"),
                        LastCategory = state.LastCategory ?? "",
                        LastEndpoint = state.LastEndpoint ?? "",
                        LastReason = state.LastReason ?? "",
                        WindowCount = state.WindowCount
                    });
                }
            }

            return snapshots
                .OrderByDescending(item => item.Disabled)
                .ThenByDescending(item => item.Score)
                .ThenByDescending(item => item.ThrottleMilliseconds)
                .Take(Math.Max(1, limit))
                .ToList();
        }

        private EndpointSecurityDecision ApplyEvent(
            string clientIp,
            string category,
            string endpoint,
            double severity,
            int rateLimitPerMinute,
            bool administrative,
            bool suspicious,
            string reason,
            EndpointSecurityOptions options) {

            var state = _clients.GetOrAdd(clientIp, _ => new EndpointSecurityClientState());
            PruneIfNeeded(options);

            lock (state.SyncRoot) {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                ApplyDecay(state, now, options);

                if (state.DisabledUntilUtc > now) {
                    return BuildBlockedDecision(clientIp, category, endpoint, reason, state, administrative, suspicious, now, "Remote IP is temporarily disabled.");
                }

                if (state.WindowStartedUtc == default || (now - state.WindowStartedUtc).TotalSeconds >= 60.0) {
                    state.WindowStartedUtc = now;
                    state.WindowCount = 0;
                }

                int safeRateLimit = Math.Max(1, rateLimitPerMinute);
                state.WindowCount++;

                double secondsSinceLastEvent = state.LastSeenUtc == default
                    ? double.MaxValue
                    : Math.Max(0.01, (now - state.LastSeenUtc).TotalSeconds);
                double expectedIntervalSeconds = 60.0 / safeRateLimit;
                double burstReferenceSeconds = Math.Min(
                    Math.Max(0.01, options.BurstReferenceSeconds),
                    Math.Max(0.01, expectedIntervalSeconds));
                bool burstInterval = state.LastSeenUtc != default && secondsSinceLastEvent < burstReferenceSeconds;
                state.ConsecutiveBurstEvents = burstInterval
                    ? Math.Min(int.MaxValue, state.ConsecutiveBurstEvents + 1)
                    : 0;

                double frequencyMultiplier = burstInterval
                    ? Math.Min(
                        Math.Max(1.0, options.MaxFrequencyMultiplier),
                        Math.Max(1.0, burstReferenceSeconds / secondsSinceLastEvent))
                    : 1.0;

                double actionableSeverity = suspicious ? Math.Max(0.0, severity) : 0.0;

                if (state.WindowCount > safeRateLimit) {
                    double excess = (double)state.WindowCount / safeRateLimit;
                    actionableSeverity += Math.Min(60.0, Math.Max(0.0, excess - 1.0) * 12.0);
                    suspicious = true;
                    reason = AppendReason(reason, "rate " + state.WindowCount.ToString() + "/min > " + safeRateLimit.ToString() + "/min");
                }

                int burstGraceEvents = Math.Max(1, options.BurstGraceEvents);
                if (administrative)
                    burstGraceEvents = Math.Max(1, burstGraceEvents / 2);

                if (state.ConsecutiveBurstEvents > burstGraceEvents) {
                    int burstExcess = state.ConsecutiveBurstEvents - burstGraceEvents;
                    double burstSeverity = administrative ? 1.5 : 0.5;
                    actionableSeverity += Math.Min(20.0, burstSeverity * Math.Max(1, burstExcess));
                    suspicious = suspicious || administrative || burstExcess >= burstGraceEvents;
                    reason = AppendReason(reason, "burst " + state.ConsecutiveBurstEvents.ToString() + " events faster than " + burstReferenceSeconds.ToString("0.###") + "s");
                }

                double increment = Math.Max(0.0, actionableSeverity) * frequencyMultiplier;
                if (increment > 0.0) {
                    state.ScoredEventCount = Math.Min(int.MaxValue, state.ScoredEventCount + 1);
                    state.Score = Math.Min(options.DisableScoreThreshold * 2.0, Math.Max(0.0, state.Score + increment));
                    state.ThrottleMs = Math.Min(
                        Math.Max(1, options.MaxThrottleMs),
                        Math.Max(0.0, state.ThrottleMs + (increment * Math.Max(0.1, options.ThrottleMsPerScore))));
                }

                state.LastSeenUtc = now;
                state.LastCategory = category ?? "";
                state.LastEndpoint = endpoint ?? "";
                state.LastReason = reason ?? "";

                int minimumDisableEvents = Math.Max(1, options.MinimumScoredEventsBeforeDisable);
                if (state.Score >= Math.Max(1.0, options.DisableScoreThreshold) && state.ScoredEventCount >= minimumDisableEvents) {
                    double ratio = state.Score / Math.Max(1.0, options.DisableScoreThreshold);
                    int disableSeconds = (int)Math.Round(Math.Min(
                        Math.Max(1, options.MaxDisableSeconds),
                        Math.Max(Math.Max(1, options.MinDisableSeconds), options.MinDisableSeconds * ratio)));
                    state.DisabledUntilUtc = now.AddSeconds(disableSeconds);
                    return BuildBlockedDecision(clientIp, category, endpoint, reason, state, administrative, true, now, "Remote IP is temporarily disabled.");
                }

                int delay = state.Score >= Math.Max(0.0, options.MinimumScoreForThrottle)
                    && state.ThrottleMs >= Math.Max(0, options.MinimumEnforcedThrottleMs)
                    ? (int)Math.Round(Math.Min(options.MaxThrottleMs, Math.Max(0.0, state.ThrottleMs)))
                    : 0;

                return new EndpointSecurityDecision {
                    Allowed = true,
                    ClientIp = clientIp,
                    Category = category ?? "",
                    Endpoint = endpoint ?? "",
                    Reason = reason ?? "",
                    IsAdministrative = administrative,
                    IsSuspicious = suspicious,
                    DelayMilliseconds = delay,
                    Score = state.Score,
                    ThrottleMilliseconds = state.ThrottleMs
                };
            }
        }

        private static EndpointSecurityDecision BuildBlockedDecision(
            string clientIp,
            string category,
            string endpoint,
            string reason,
            EndpointSecurityClientState state,
            bool administrative,
            bool suspicious,
            DateTimeOffset now,
            string message) {

            int retryAfter = state.DisabledUntilUtc > now
                ? (int)Math.Ceiling((state.DisabledUntilUtc - now).TotalSeconds)
                : 60;

            return new EndpointSecurityDecision {
                Allowed = false,
                IsDisabled = true,
                IsAdministrative = administrative,
                IsSuspicious = suspicious,
                StatusCode = 403,
                ReasonPhrase = "Forbidden",
                Message = message ?? "Remote IP is temporarily disabled.",
                ClientIp = clientIp ?? "",
                Category = category ?? "",
                Endpoint = endpoint ?? "",
                Reason = reason ?? "",
                Score = state.Score,
                ThrottleMilliseconds = state.ThrottleMs,
                RetryAfterSeconds = Math.Max(1, retryAfter),
                DisabledUntilUtc = state.DisabledUntilUtc
            };
        }

        private static void ApplyDecay(EndpointSecurityClientState state, DateTimeOffset now, EndpointSecurityOptions options) {
            if (state.LastSeenUtc == default)
                return;

            double elapsedSeconds = Math.Max(0.0, (now - state.LastSeenUtc).TotalSeconds);
            if (elapsedSeconds <= 0.0)
                return;

            state.Score = Math.Max(0.0, state.Score - (elapsedSeconds * Math.Max(0.0, options.ScoreDecayPerSecond)));
            state.ThrottleMs = Math.Max(0.0, state.ThrottleMs - (elapsedSeconds * Math.Max(0.0, options.ThrottleDecayMsPerSecond)));
            if (state.Score <= 0.01) {
                state.Score = 0.0;
                state.ScoredEventCount = 0;
            }
        }

        private void PruneIfNeeded(EndpointSecurityOptions options) {
            int max = Math.Max(128, options.MaxTrackedClients);
            if (_clients.Count <= max)
                return;

            DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddMinutes(-30);
            foreach (var kv in _clients) {
                if (_clients.Count <= max)
                    return;

                EndpointSecurityClientState state = kv.Value;
                bool remove;
                lock (state.SyncRoot) {
                    remove = state.DisabledUntilUtc <= DateTimeOffset.UtcNow
                        && state.Score <= 0.01
                        && state.ThrottleMs <= 0.01
                        && state.LastSeenUtc < cutoff;
                }
                if (remove)
                    _clients.TryRemove(kv.Key, out _);
            }
        }

        private static double ClassifyHttpRequest(HttpRequest request, EndpointSecurityOptions options, out bool administrative, out bool suspicious, out string reason) {
            administrative = IsAdministrativeHttpEndpoint(request?.Path ?? "");
            suspicious = false;
            reason = "";

            double severity = administrative ? 0.75 : 0.15;
            string method = (request?.Method ?? "").Trim().ToUpperInvariant();
            string path = request?.Path ?? "";
            string query = request?.QueryString ?? "";
            string pathAndQuery = string.IsNullOrEmpty(query) ? path : path + "?" + query;

            if (method == "TRACE" || method == "CONNECT") {
                severity += 25.0;
                suspicious = true;
                reason = AppendReason(reason, "unsafe method " + method);
            }

            if (options.BlockKnownBadPaths && IsKnownBadHttpPath(pathAndQuery)) {
                severity += 40.0;
                suspicious = true;
                reason = AppendReason(reason, "known scanner path");
            }

            if (ContainsPathTraversal(pathAndQuery)) {
                severity += 35.0;
                suspicious = true;
                reason = AppendReason(reason, "path traversal probe");
            }

            string userAgent = GetHeader(request, "User-Agent");
            if (options.TreatSuspiciousUserAgentsAsBad && IsSuspiciousUserAgent(userAgent)) {
                severity += administrative ? 4.0 : 2.0;
                suspicious = suspicious || administrative;
                reason = AppendReason(reason, "suspicious user agent");
            } else if (options.TreatSuspiciousUserAgentsAsBad && administrative && string.IsNullOrWhiteSpace(userAgent)) {
                severity += 2.0;
                suspicious = true;
                reason = AppendReason(reason, "missing user agent on admin endpoint");
            }

            if (request != null && request.ContentLength > 25L * 1024L * 1024L) {
                severity += 8.0;
                suspicious = true;
                reason = AppendReason(reason, "large request body");
            }

            return severity;
        }

        private static bool IsAdministrativeHttpEndpoint(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string p = path.Trim().ToLowerInvariant();
            return p == "/sql"
                || p.StartsWith("/sql/", StringComparison.Ordinal)
                || p.IndexOf("admin", StringComparison.Ordinal) >= 0
                || p.IndexOf("permission", StringComparison.Ordinal) >= 0
                || p.IndexOf("filesystem", StringComparison.Ordinal) >= 0
                || p.IndexOf("terminal", StringComparison.Ordinal) >= 0
                || p.IndexOf("observability", StringComparison.Ordinal) >= 0
                || p.IndexOf("trust-abuse", StringComparison.Ordinal) >= 0
                || p.IndexOf("security", StringComparison.Ordinal) >= 0
                || p.IndexOf("ftp-config", StringComparison.Ordinal) >= 0
                || p.IndexOf("chat-client-admin", StringComparison.Ordinal) >= 0
                || p.IndexOf("chat-sql-admin", StringComparison.Ordinal) >= 0
                || p.IndexOf("developer-sdk", StringComparison.Ordinal) >= 0
                || p.IndexOf("developer-project-workflow", StringComparison.Ordinal) >= 0
                || p.IndexOf("model-runtime", StringComparison.Ordinal) >= 0
                || p.IndexOf("hardware-attestation", StringComparison.Ordinal) >= 0
                || p.IndexOf("token-rate-request", StringComparison.Ordinal) >= 0
                || p.IndexOf("web-auth", StringComparison.Ordinal) >= 0
                || p.IndexOf("payments", StringComparison.Ordinal) >= 0
                || p.IndexOf("payouts", StringComparison.Ordinal) >= 0
                || p.Equals("/api/account", StringComparison.Ordinal)
                || p.Equals("/api/costs", StringComparison.Ordinal);
        }

        private static bool IsKnownBadHttpPath(string pathAndQuery) {
            if (string.IsNullOrWhiteSpace(pathAndQuery))
                return false;

            string p = pathAndQuery.ToLowerInvariant();
            return p.Contains("/.env")
                || p.Contains("/.git")
                || p.Contains("/.aws")
                || p.Contains("/.ssh")
                || p.Contains("/wp-admin")
                || p.Contains("wp-login")
                || p.Contains("phpmyadmin")
                || p.Contains("/cgi-bin")
                || p.Contains("/vendor/phpunit")
                || p.Contains("/actuator")
                || p.Contains("/manager/html")
                || p.Contains("/server-status")
                || p.Contains("etc/passwd")
                || p.Contains("id_rsa")
                || p.Contains("appsettings.json")
                || p.Contains("web.config")
                || p.Contains(".npmrc")
                || p.Contains("cmd=")
                || p.Contains("powershell")
                || p.Contains("/bin/sh")
                || p.Contains("eval(")
                || p.Contains("' or '1'='1")
                || p.Contains("union select");
        }

        private static bool ContainsPathTraversal(string value) {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            string decoded = value;
            try { decoded = Uri.UnescapeDataString(value); } catch { }
            decoded = decoded.Replace('\\', '/').ToLowerInvariant();
            return decoded.Contains("../")
                || decoded.Contains("/..")
                || decoded.Contains("%2e%2e")
                || decoded.Contains("..%2f")
                || decoded.Contains("%2f..");
        }

        private static bool IsSuspiciousFilesystemPath(string path) {
            if (string.IsNullOrWhiteSpace(path))
                return true;

            string p = path.ToLowerInvariant();
            return ContainsPathTraversal(path)
                || p.Contains(".htaccess")
                || p.Contains(".env")
                || p.Contains(".git")
                || p.Contains("appsettings.json")
                || p.Contains("web.config");
        }

        private static bool IsSuspiciousUserAgent(string userAgent) {
            if (string.IsNullOrWhiteSpace(userAgent))
                return false;

            string ua = userAgent.ToLowerInvariant();
            return ua.Contains("sqlmap")
                || ua.Contains("nikto")
                || ua.Contains("nmap")
                || ua.Contains("masscan")
                || ua.Contains("zgrab")
                || ua.Contains("acunetix")
                || ua.Contains("nessus")
                || ua.Contains("wpscan")
                || ua.Contains("python-requests")
                || ua.Contains("curl/")
                || ua.Contains("wget/")
                || ua.Contains("go-http-client");
        }

        private static string GetHeader(HttpRequest request, string name) {
            if (request?.Headers == null || string.IsNullOrWhiteSpace(name))
                return "";
            return request.Headers.TryGetValue(name, out string value) ? value ?? "" : "";
        }

        private static string ResolveClientIp(NetworkConnection connection, HttpRequest request, EndpointSecurityOptions options) {
            if (options != null && options.TrustForwardedForHeaders && request?.Headers != null) {
                if (request.Headers.TryGetValue("X-Forwarded-For", out string xff)) {
                    string first = (xff ?? "").Split(',').Select(item => item.Trim()).FirstOrDefault(item => item.Length > 0);
                    string normalized = NormalizeClientIp(first);
                    if (!string.IsNullOrWhiteSpace(normalized))
                        return normalized;
                }

                if (request.Headers.TryGetValue("X-Real-IP", out string realIp)) {
                    string normalized = NormalizeClientIp(realIp);
                    if (!string.IsNullOrWhiteSpace(normalized))
                        return normalized;
                }
            }

            return ResolveClientIp(connection);
        }

        private static string ResolveClientIp(NetworkConnection connection) {
            try {
                if (connection?.EndPoint?.Address != null)
                    return NormalizeClientIp(connection.EndPoint.Address.ToString());
            } catch { }

            try {
                if (connection?.Socket?.RemoteEndPoint is IPEndPoint ep && ep.Address != null)
                    return NormalizeClientIp(ep.Address.ToString());
            } catch { }

            return "";
        }

        private static string NormalizeClientIp(string clientIp) {
            if (string.IsNullOrWhiteSpace(clientIp))
                return "";

            clientIp = clientIp.Trim();
            if (clientIp.StartsWith("[", StringComparison.Ordinal)) {
                int close = clientIp.IndexOf(']');
                if (close > 0)
                    clientIp = clientIp.Substring(1, close - 1);
            } else {
                int colon = clientIp.LastIndexOf(':');
                if (colon > 0 && clientIp.IndexOf(':') == colon)
                    clientIp = clientIp.Substring(0, colon);
            }

            return clientIp.Trim();
        }

        private static bool ShouldTrackClient(string clientIp, EndpointSecurityOptions options) {
            if (options == null || !options.Enabled)
                return false;
            if (string.IsNullOrWhiteSpace(clientIp))
                return false;
            if (!options.ExemptLoopback)
                return true;
            if (!IPAddress.TryParse(clientIp, out IPAddress address))
                return true;
            return !IPAddress.IsLoopback(address);
        }

        private static EndpointSecurityOptions NormalizeOptions(EndpointSecurityOptions options) {
            return options ?? new EndpointSecurityOptions();
        }

        private static string AppendReason(string current, string next) {
            if (string.IsNullOrWhiteSpace(next))
                return current ?? "";
            if (string.IsNullOrWhiteSpace(current))
                return next;
            return current + "; " + next;
        }

        private sealed class EndpointSecurityClientState {
            public readonly object SyncRoot = new object();
            public double Score;
            public double ThrottleMs;
            public DateTimeOffset LastSeenUtc;
            public DateTimeOffset DisabledUntilUtc;
            public DateTimeOffset WindowStartedUtc;
            public int WindowCount;
            public int ConsecutiveBurstEvents;
            public int ScoredEventCount;
            public string LastCategory = "";
            public string LastEndpoint = "";
            public string LastReason = "";
        }
    }
}
