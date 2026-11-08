using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Reports configuration mistakes at startup instead of on the first affected request.
/// Option values are checked in <see cref="StartingAsync"/>, ahead of every hosted service,
/// because the cleanup service would otherwise act on them first. Endpoints are checked in
/// <see cref="StartedAsync"/> because they aren't complete until the web host has started,
/// and an exception thrown there still fails host startup.
/// </summary>
internal sealed class IdempotencyStartupValidator : IHostedLifecycleService
{
    private const int MaxRoutesNamedInWarning = 20;

    private readonly IdempotencyOptions _options;
    private readonly IEnumerable<EndpointDataSource> _endpointSources;
    private readonly IHostEnvironment _environment;
    private readonly IFingerprintBuilder _fingerprintBuilder;
    private readonly ILogger<IdempotencyStartupValidator> _logger;

    public IdempotencyStartupValidator(
        IOptions<IdempotencyOptions> options,
        IEnumerable<EndpointDataSource> endpointSources,
        IHostEnvironment environment,
        IFingerprintBuilder fingerprintBuilder,
        ILogger<IdempotencyStartupValidator> logger)
    {
        _options = options.Value;
        _endpointSources = endpointSources;
        _environment = environment;
        _fingerprintBuilder = fingerprintBuilder;
        _logger = logger;
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        ThrowIfOptionsAreInvalid();
        return Task.CompletedTask;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        WarnIfKeysAreUnscoped();

        if (_options.ValidateEndpointsOnStartup ?? _environment.IsDevelopment())
        {
            // This read is the expensive part: it makes ASP.NET Core build every endpoint.
            var endpoints = _endpointSources.SelectMany(source => source.Endpoints).ToList();
            ThrowIfAnyEndpointUsesUnknownPolicy(endpoints);
            ThrowIfFingerprintPropertiesCannotBeRead(endpoints);
            WarnIfAnyMarkedRouteIsNotEnforced(endpoints);
            WarnIfAnyRouteIsUnscoped(endpoints);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Several of these values look harmless and silently remove the duplicate protection, like
    /// a zero <see cref="IdempotencyOptions.ResponseTtl"/>, so they fail rather than warn. All
    /// problems are reported together, so that fixing one doesn't just reveal the next.
    /// </summary>
    private void ThrowIfOptionsAreInvalid()
    {
        var problems = new List<string>();

        if (_options.KeyPrefix?.Length > IdempotencyStoreKey.MaxPrefixLength)
        {
            problems.Add($"KeyPrefix is {_options.KeyPrefix.Length} characters, over the limit of {IdempotencyStoreKey.MaxPrefixLength}.");
        }

        if (_options.UnmarkedEndpointSettingsConflict)
        {
            problems.Add("MissingKeyPolicy and UnmarkedEndpoints ask for different behaviour. MissingKeyPolicy is deprecated, so set only UnmarkedEndpoints.");
        }

        if (string.IsNullOrWhiteSpace(_options.HeaderName))
        {
            problems.Add("HeaderName must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(_options.ReplayedHeaderName))
        {
            problems.Add("ReplayedHeaderName must not be empty.");
        }

        if (_options.EnforcedMethods is null)
        {
            problems.Add("EnforcedMethods must not be null.");
        }

        AddIfNotPositive(problems, nameof(IdempotencyOptions.ClaimTtl), _options.ClaimTtl);
        AddIfNotPositive(problems, nameof(IdempotencyOptions.ResponseTtl), _options.ResponseTtl);

        if (_options.MaxResponseBodySize <= 0)
        {
            problems.Add($"MaxResponseBodySize is {_options.MaxResponseBodySize}, but must be greater than zero, or every retry gets a 410.");
        }

        if (_options.Cleanup is not { } cleanup)
        {
            problems.Add("Cleanup must not be null.");
        }
        else
        {
            AddIfNotPositive(problems, "Cleanup.Interval", cleanup.Interval);

            if (cleanup.BatchSize <= 0)
            {
                problems.Add($"Cleanup.BatchSize is {cleanup.BatchSize}, but must be greater than zero.");
            }

            if (cleanup.MaxIterationsPerSweep <= 0)
            {
                problems.Add($"Cleanup.MaxIterationsPerSweep is {cleanup.MaxIterationsPerSweep}, but must be greater than zero.");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "IdempotencyOptions has invalid settings: " + string.Join(" ", problems));
        }
    }

    private static void AddIfNotPositive(List<string> problems, string name, TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            problems.Add($"{name} is {value}, but must be greater than zero.");
        }
    }

    private void ThrowIfAnyEndpointUsesUnknownPolicy(List<Endpoint> endpoints)
    {
        foreach (var endpoint in endpoints)
        {
            foreach (var attribute in endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>())
            {
                if (attribute.Policy is { } name && !_options.TryGetPolicy(name, out _))
                {
                    throw new InvalidOperationException(
                        $"Endpoint '{endpoint.DisplayName}' uses idempotency policy '{name}', which is not registered. " +
                        "Register it with IdempotencyOptions.AddPolicy.");
                }
            }
        }
    }

    /// <summary>
    /// With no body read, <see cref="IdempotentAttribute.FingerprintProperties"/> has nothing to
    /// hash, so a retry with a different amount replays the first response instead of getting a
    /// 422. A custom fingerprint builder may not use the size limit at all, so it's left alone.
    /// </summary>
    private void ThrowIfFingerprintPropertiesCannotBeRead(List<Endpoint> endpoints)
    {
        if (_options.MaxFingerprintBodySize > 0 || _fingerprintBuilder is not DefaultFingerprintBuilder)
        {
            return;
        }

        foreach (var endpoint in endpoints)
        {
            var attribute = IdempotentAttribute.Merge(endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>());
            if (attribute?.FingerprintProperties is { Length: > 0 })
            {
                throw new InvalidOperationException(
                    $"Endpoint '{endpoint.DisplayName}' sets FingerprintProperties, but MaxFingerprintBodySize is " +
                    $"{_options.MaxFingerprintBodySize}, so the body is never read and a changed payload goes undetected. " +
                    "Raise MaxFingerprintBodySize or remove FingerprintProperties.");
            }
        }
    }

    /// <summary>
    /// <see cref="IdempotencyOptions.EnforcedMethods"/> applies before an endpoint's marker is
    /// read, so a marked <c>DELETE</c> looks protected and isn't. Read methods are left out,
    /// because <c>[Idempotent]</c> on a controller also marks its <c>GET</c> actions.
    /// </summary>
    private void WarnIfAnyMarkedRouteIsNotEnforced(List<Endpoint> endpoints)
    {
        var unenforced = endpoints.Where(IsMarkedButNotEnforced).Select(endpoint => endpoint.DisplayName).ToList();
        if (unenforced.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "{Count} route(s) are marked as idempotent, but their HTTP methods aren't in EnforcedMethods, so the guard " +
            "skips them and duplicates aren't caught. Add the methods to IdempotencyOptions.EnforcedMethods: {Routes}",
            unenforced.Count,
            string.Join(", ", unenforced.Take(MaxRoutesNamedInWarning)));
    }

    private bool IsMarkedButNotEnforced(Endpoint endpoint)
    {
        if (endpoint.Metadata.GetMetadata<IdempotentAttribute>() is null)
        {
            return false;
        }

        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        return methods is { Count: > 0 }
            && !methods.Any(method => _options.EnforcedMethods.Contains(method, StringComparer.OrdinalIgnoreCase))
            && methods.Any(method => !IsReadMethod(method));
    }

    private static bool IsReadMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);

    private void WarnIfKeysAreUnscoped()
    {
        if (_options.KeyScope is null && !_options.AnyPolicyDefinesKeyScope && !_options.SuppressUnscopedKeysWarning)
        {
            _logger.LogWarning(
                "Idempotency keys are not scoped to a caller, so any caller who presents a key can replay the response " +
                "stored under it. Set IdempotencyOptions.KeyScope on multi-caller APIs, or set " +
                "SuppressUnscopedKeysWarning if a single trusted caller owns the key space.");
        }
    }

    /// <summary>
    /// An app that scopes some routes looks finished, so the warning about having no scope at
    /// all doesn't fire, but callers on its other routes still share one key space. An
    /// <see cref="IdempotencyOptions.EndpointFilter"/> takes request paths rather than route
    /// patterns, so with one set, there's no telling which routes it guards.
    /// </summary>
    private void WarnIfAnyRouteIsUnscoped(List<Endpoint> endpoints)
    {
        if (_options.KeyScope is not null
            || !_options.AnyPolicyDefinesKeyScope
            || _options.SuppressUnscopedKeysWarning
            || _options.EndpointFilter is not null)
        {
            return;
        }

        var unscoped = endpoints.Where(IsGuardedWithoutScope).Select(endpoint => endpoint.DisplayName).ToList();
        if (unscoped.Count == 0)
        {
            return;
        }

        _logger.LogWarning(
            "{Count} route(s) have no idempotency key scope while other routes do, so callers on them share one key " +
            "space and can replay each other's responses. Give them a policy with a KeyScope, set " +
            "IdempotencyOptions.KeyScope, or set SuppressUnscopedKeysWarning if that's intended: {Routes}",
            unscoped.Count,
            string.Join(", ", unscoped.Take(MaxRoutesNamedInWarning)));
    }

    private bool IsGuardedWithoutScope(Endpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (methods is { Count: > 0 } && !methods.Any(method => _options.EnforcedMethods.Contains(method, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        var attribute = IdempotentAttribute.Merge(endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>());
        if (_options.UnmarkedEndpoints == UnmarkedEndpointBehavior.Ignore && attribute is null)
        {
            return false;
        }

        return attribute?.Policy is not { } name
            || !_options.TryGetPolicy(name, out var policy)
            || policy!.KeyScope is null;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
