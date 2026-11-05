using Microsoft.AspNetCore.Http;

namespace IdempotencyGuard;

public class IdempotencyOptions
{
    private readonly Dictionary<string, IdempotencyPolicy> _policies = new(StringComparer.Ordinal);

    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// Name of the response header added to replayed (cached) responses to indicate
    /// the response was served from the idempotency store rather than re-executing the handler.
    /// Default: <c>"X-Idempotent-Replayed"</c>.
    /// </summary>
    public string ReplayedHeaderName { get; set; } = "X-Idempotent-Replayed";

    public TimeSpan ClaimTtl { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan ResponseTtl { get; set; } = TimeSpan.FromHours(24);

    public ConcurrentRequestPolicy ConcurrentRequestPolicy { get; set; } = ConcurrentRequestPolicy.WaitThenReplay;

    public TimeSpan ConcurrentRequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public MissingKeyPolicy MissingKeyPolicy { get; set; } = MissingKeyPolicy.Allow;

    public HashSet<string> EnforcedMethods { get; set; } = ["POST", "PUT", "PATCH"];

    /// <summary>
    /// Largest response body that can be stored, in bytes. A larger response still reaches
    /// the client, and <see cref="OversizedResponse"/> decides what a retry gets.
    /// The check runs after the handler finishes, so this doesn't limit how much is buffered.
    /// Default: 1 MB.
    /// </summary>
    public int MaxResponseBodySize { get; set; } = 1_048_576;

    /// <summary>
    /// What a retry gets when the response was too large to store. Default:
    /// <see cref="OversizedResponseBehavior.RejectRetries"/>.
    /// </summary>
    public OversizedResponseBehavior OversizedResponse { get; set; } = OversizedResponseBehavior.RejectRetries;

    /// <summary>
    /// Maximum number of request body bytes used for fingerprint hashing.
    /// Bodies larger than this are fingerprinted using only the first N bytes.
    /// Default: 1 MB. Set to 0 to skip body hashing entirely, in which case the body isn't read.
    /// </summary>
    public int MaxFingerprintBodySize { get; set; } = 1_048_576;

    /// <summary>
    /// Prefix prepended to every idempotency key before it is passed to the store.
    /// Use this to namespace keys by environment or tenant (e.g. <c>"staging:"</c>, <c>"tenant-42:"</c>).
    /// It stays readable in the store, ahead of the digest, so prefix scans still work. Limited to
    /// <see cref="IdempotencyStoreKey.MaxPrefixLength"/> characters. End it with a delimiter, or
    /// <c>tenant-1</c> with key <c>2abc</c> gets the same downstream key as <c>tenant-12</c> with <c>abc</c>.
    /// Default: <c>null</c> (no prefix). Applied across all store implementations.
    /// </summary>
    public string? KeyPrefix { get; set; }

    /// <summary>
    /// Longest idempotency key accepted from a client, in characters. A longer key is rejected
    /// with a 400. Stores only see a fixed-length digest, so this is a sanity limit and not a
    /// storage limit. Default: 1024. Set to 0 to disable the check.
    /// </summary>
    public int MaxKeyLength { get; set; } = 1024;

    /// <summary>
    /// The <see cref="HttpContext.Items"/> key used to read
    /// a per-request key prefix override. When the item is present and is a non-null
    /// <see cref="string"/>, it takes precedence over <see cref="KeyPrefix"/>.
    /// Default: <c>"IdempotencyKeyPrefix"</c>.
    /// </summary>
    public string KeyPrefixItemKey { get; set; } = DefaultKeyPrefixItemKey;

    /// <summary>
    /// Default value for <see cref="KeyPrefixItemKey"/>.
    /// </summary>
    public const string DefaultKeyPrefixItemKey = "IdempotencyKeyPrefix";

    /// <summary>
    /// Resolves who a key belongs to, normally whoever generates and retries it: a user ID,
    /// or a client or account ID for service callers. Keys from different scopes never
    /// collide, so one caller can't replay another's response.
    /// Returning <c>null</c> rejects the request with a 403 rather than falling back to the
    /// shared key space. Default: <c>null</c> (no scoping, every caller shares one key space).
    /// A policy's <see cref="IdempotencyPolicy.KeyScope"/> takes precedence over this value.
    /// </summary>
    public Func<HttpContext, string?>? KeyScope { get; set; }

    /// <summary>
    /// Decides from the status code whether a response is stored and replayed. When it
    /// returns <c>false</c> the claim is released and a retry runs the handler again.
    /// Default: <c>null</c> (store every response, including 5xx).
    /// A policy's <see cref="IdempotencyPolicy.ShouldStoreResponse"/> takes precedence over this value.
    /// </summary>
    public Func<int, bool>? ShouldStoreResponse { get; set; }

    /// <summary>
    /// Whether to check endpoints at startup: that named policies are registered, and that no
    /// route is left unscoped while others have a scope. Default: <c>null</c>, which checks in the
    /// Development environment only, like <c>ValidateOnBuild</c>.
    /// <para>
    /// Reading the endpoints makes ASP.NET Core build them, and routing builds its own copy on the
    /// first request, so this adds about 1 ms per minimal API endpoint with typed parameters to
    /// startup. With the check off, an unregistered policy fails each request that uses it with an
    /// <see cref="InvalidOperationException"/> before the handler runs.
    /// </para>
    /// </summary>
    public bool? ValidateEndpointsOnStartup { get; set; }

    /// <summary>
    /// Silences the startup warnings about unscoped keys: the one logged when no
    /// <see cref="KeyScope"/> is configured, and the one listing unscoped routes.
    /// Set this when a single trusted caller owns the whole key space.
    /// </summary>
    public bool SuppressUnscopedKeysWarning { get; set; }

    /// <summary>
    /// Disables the check that authorization ran before the guard on endpoints that
    /// require it. Leave this off unless authorization is enforced some other way:
    /// a guard that runs first replays stored responses to unauthorized callers.
    /// </summary>
    public bool SuppressAuthorizationOrderCheck { get; set; }

    /// <summary>
    /// Registers a named policy that endpoints select with
    /// <see cref="AspNetCore.IdempotentAttribute.Policy"/>. Anything the policy leaves
    /// unset falls back to the global value on these options.
    /// </summary>
    public IdempotencyOptions AddPolicy(string name, Action<IdempotencyPolicy> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);

        var policy = new IdempotencyPolicy();
        configure(policy);

        if (!_policies.TryAdd(name, policy))
        {
            throw new InvalidOperationException($"An idempotency policy named '{name}' is already registered.");
        }

        return this;
    }

    internal bool TryGetPolicy(string name, out IdempotencyPolicy? policy) =>
        _policies.TryGetValue(name, out policy);

    internal bool AnyPolicyDefinesKeyScope => _policies.Values.Any(policy => policy.KeyScope is not null);

    public bool Enabled { get; set; } = true;

    public Func<HttpMethod, string, bool>? EndpointFilter { get; set; }

    /// <summary>
    /// Custom error response factory. When set, the middleware calls this to produce the
    /// JSON body for all error responses (400, 403, 409, 410, 422) instead of the default
    /// <c>{"error":"..."}</c> format. The returned object is serialized with System.Text.Json.
    /// </summary>
    public Func<IdempotencyProblem, object>? ErrorResponseFactory { get; set; }

    /// <summary>
    /// Additional response headers to exclude from storage beyond the built-in defaults
    /// (hop-by-hop + transient headers). Ignored if <see cref="HeaderAllowList"/> is set.
    /// Uses case-insensitive comparison.
    /// </summary>
    public HashSet<string>? HeaderDenyList { get; set; }

    /// <summary>
    /// If set, ONLY these response headers will be stored and replayed.
    /// Overrides <see cref="HeaderDenyList"/> and the built-in defaults entirely.
    /// Uses case-insensitive comparison.
    /// </summary>
    public HashSet<string>? HeaderAllowList { get; set; }

    /// <summary>
    /// Configuration for the background expired-entry cleanup service.
    /// </summary>
    public CleanupOptions Cleanup { get; set; } = new();
}

/// <summary>
/// Per-endpoint overrides selected by name. Unset values fall back to the matching
/// value on <see cref="IdempotencyOptions"/>.
/// </summary>
public sealed class IdempotencyPolicy
{
    /// <inheritdoc cref="IdempotencyOptions.KeyScope"/>
    public Func<HttpContext, string?>? KeyScope { get; set; }

    /// <inheritdoc cref="IdempotencyOptions.ShouldStoreResponse"/>
    public Func<int, bool>? ShouldStoreResponse { get; set; }

    /// <inheritdoc cref="IdempotencyOptions.OversizedResponse"/>
    public OversizedResponseBehavior? OversizedResponse { get; set; }
}

public static class HeaderFilter
{
    /// <summary>
    /// Hop-by-hop headers (RFC 9110 §7.6.1) plus common transient headers
    /// that must not be stored or replayed.
    /// </summary>
    public static readonly HashSet<string> DefaultDenyList = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hop-by-hop (RFC 9110)
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade",

        // Transient / session-specific
        "Set-Cookie",
        "WWW-Authenticate",
        "Proxy-Connection",
        "Alt-Svc",

        // Server-generated per-response
        "Server",
        "Date",
    };

    public static bool ShouldStoreHeader(string headerName, IdempotencyOptions options)
    {
        if (options.HeaderAllowList is { Count: > 0 })
        {
            return Equals(options.HeaderAllowList.Comparer, StringComparer.OrdinalIgnoreCase)
                ? options.HeaderAllowList.Contains(headerName)
                : options.HeaderAllowList.Any(
                    h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase));
        }

        if (DefaultDenyList.Contains(headerName))
            return false;

        if (options.HeaderDenyList is { Count: > 0 })
        {
            return Equals(options.HeaderDenyList.Comparer, StringComparer.OrdinalIgnoreCase)
                ? !options.HeaderDenyList.Contains(headerName)
                : !options.HeaderDenyList.Any(
                    h => string.Equals(h, headerName, StringComparison.OrdinalIgnoreCase));
        }

        return true;
    }
}

public enum ConcurrentRequestPolicy
{
    WaitThenReplay,
    ReturnConflict
}

public enum MissingKeyPolicy
{
    Allow,
    Reject
}

/// <summary>
/// What a retry gets when the response was larger than
/// <see cref="IdempotencyOptions.MaxResponseBodySize"/>.
/// </summary>
public enum OversizedResponseBehavior
{
    /// <summary>
    /// Retries get a 410 with the original status, and the handler doesn't run again. A client
    /// that lost the response can't get it back. Once <c>ResponseTtl</c> passes the key is free again.
    /// </summary>
    RejectRetries,

    /// <summary>
    /// The claim is released, so a retry runs the handler again. Only for endpoints
    /// that are safe to rerun.
    /// </summary>
    RunHandlerAgain
}

public enum IdempotencyErrorKind
{
    MissingKey,
    FingerprintMismatch,
    Conflict,
    Timeout,
    MissingScope,
    InvalidKey,
    ResponseNotReplayable
}

public class IdempotencyProblem
{
    public required int StatusCode { get; init; }
    public required IdempotencyErrorKind Kind { get; init; }
    public required string Message { get; init; }
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// The status the original request completed with, when <see cref="Kind"/> is
    /// <see cref="IdempotencyErrorKind.ResponseNotReplayable"/>.
    /// </summary>
    public int? OriginalStatusCode { get; init; }
}

/// <summary>
/// Options for the background expired-entry cleanup service.
/// </summary>
public class CleanupOptions
{
    /// <summary>
    /// Whether the background cleanup hosted service is enabled. Default: <c>true</c>.
    /// Set to <c>false</c> to disable automatic cleanup (e.g. if using an external scheduler
    /// or calling <see cref="IPurgableIdempotencyStore.PurgeExpiredAsync"/> directly).
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Time between cleanup sweeps. Default: 5 minutes.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum entries to delete per database round-trip. Default: 1000.
    /// </summary>
    public int BatchSize { get; set; } = 1_000;

    /// <summary>
    /// Maximum batch iterations per sweep to prevent runaway loops. Default: 100.
    /// After this many iterations the sweep yields until the next interval.
    /// </summary>
    public int MaxIterationsPerSweep { get; set; } = 100;
}
