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
    private readonly IdempotencyOptions _options;
    private readonly IEnumerable<EndpointDataSource> _endpointSources;
    private readonly ILogger<IdempotencyStartupValidator> _logger;

    public IdempotencyStartupValidator(
        IOptions<IdempotencyOptions> options,
        IEnumerable<EndpointDataSource> endpointSources,
        ILogger<IdempotencyStartupValidator> logger)
    {
        _options = options.Value;
        _endpointSources = endpointSources;
        _logger = logger;
    }

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfKeyPrefixIsTooLong();
        ThrowIfAnyEndpointUsesUnknownPolicy();
        WarnIfKeysAreUnscoped();
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

    private void ThrowIfAnyEndpointUsesUnknownPolicy()
    {
        foreach (var endpoint in _endpointSources.SelectMany(source => source.Endpoints))
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

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
