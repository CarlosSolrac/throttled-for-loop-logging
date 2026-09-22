using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ThrottledLogging;

/// <summary>Registers <see cref="IOperationLogger"/> and <see cref="IOperationRegistry"/>.</summary>
public static class ThrottledLoggingServiceCollectionExtensions
{
    /// <summary>Adds throttled operation logging with the default settings.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddThrottledLogging(this IServiceCollection services)
        => services.AddThrottledLogging(static _ => { });

    /// <summary>Adds throttled operation logging and configures it.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Applied to the options.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IServiceCollection AddThrottledLogging(this IServiceCollection services, Action<ThrottledLoggingOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.AddOptions<ThrottledLoggingOptions>().Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        // Built explicitly: OperationLogger offers a second, non-DI constructor, and the container
        // cannot choose between them on its own.
        services.TryAddSingleton(static provider => new OperationLogger(
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<IOptions<ThrottledLoggingOptions>>(),
            provider.GetRequiredService<TimeProvider>()));

        // One instance behind both interfaces: the registry must see the operations the factory
        // created, and two singletons would each see only their own.
        services.TryAddSingleton<IOperationLogger>(static provider => provider.GetRequiredService<OperationLogger>());
        services.TryAddSingleton<IOperationRegistry>(static provider => provider.GetRequiredService<OperationLogger>());

        return services;
    }
}
