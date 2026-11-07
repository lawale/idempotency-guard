using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IdempotencyGuard.AspNetCore;

/// <summary>
/// Reports configuration mistakes at startup instead of on the first affected request.
/// Runs in <see cref="StartedAsync"/> because endpoints aren't complete until the web host
/// has started, and an exception thrown there still fails host startup.
/// </summary>
internal sealed class IdempotencyStartupValidator : IHostedLifecycleService
{
    private const int MaxRoutesNamedInWarning = 20;

    private readonly IdempotencyOptions _options;
    private readonly IEnumerable<EndpointDataSource> _endpointSources;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<IdempotencyStartupValidator> _logger;

    public IdempotencyStartupValidator(
        IOptions<IdempotencyOptions> options,
        IEnumerable<EndpointDataSource> endpointSources,
        IHostEnvironment environment,
        ILogger<IdempotencyStartupValidator> logger)
    {
        _options = options.Value;
        _endpointSources = endpointSources;
        _environment = environment;
        _logger = logger;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfKeyPrefixIsTooLong();
        ThrowIfUnmarkedEndpointSettingsConflict();
        WarnIfKeysAreUnscoped();

        if (_options.ValidateEndpointsOnStartup ?? _environment.IsDevelopment())
        {
            // This read is the expensive part: it makes ASP.NET Core build every endpoint.
            var endpoints = _endpointSources.SelectMany(source => source.Endpoints).ToList();
            ThrowIfAnyEndpointUsesUnknownPolicy(endpoints);
            WarnIfAnyRouteIsUnscoped(endpoints);
        }

        return Task.CompletedTask;
    }

    private void ThrowIfKeyPrefixIsTooLong()
    {
        if (_options.KeyPrefix?.Length > IdempotencyStoreKey.MaxPrefixLength)
        {
            throw new InvalidOperationException(
                $"IdempotencyOptions.KeyPrefix is {_options.KeyPrefix.Length} characters, " +
                $"over the limit of {IdempotencyStoreKey.MaxPrefixLength}.");
        }
    }

    private void ThrowIfUnmarkedEndpointSettingsConflict()
    {
        if (_options.UnmarkedEndpointSettingsConflict)
        {
            throw new InvalidOperationException(
                "IdempotencyOptions.MissingKeyPolicy and UnmarkedEndpoints ask for different behaviour. " +
                "MissingKeyPolicy is deprecated, so set only UnmarkedEndpoints.");
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

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
