using Microsoft.Extensions.Configuration;
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
        return services.AddCore();
    }

    /// <summary>
    /// Adds throttled operation logging with its settings read from configuration, typically a
    /// section of appsettings.json, and reloaded when that changes.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section to bind, e.g. <c>builder.Configuration.GetSection("ThrottledLogging")</c>.</param>
    /// <param name="configure">
    /// Applied after binding, and again after every reload. The place for what configuration cannot
    /// hold, such as <see cref="ThrottledLoggingOptions.OnEmitted"/>.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// A reload applies to operations begun after it; each running operation keeps the settings it
    /// began with. Changing <see cref="ThrottledLoggingOptions.EnableSweeper"/> starts or stops the
    /// sweeper. Settings that fail validation are logged (event id 9011) and ignored, leaving the
    /// last good ones in force.
    /// </remarks>
    public static IServiceCollection AddThrottledLogging(this IServiceCollection services, IConfiguration configuration, Action<ThrottledLoggingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // What OptionsBuilder.Bind does, except that a value the binder cannot convert is recorded
        // rather than thrown. Thrown, it would escape from the options monitor into whatever raised
        // the reload (a file watcher, in most hosts), before the logger could reject it and keep
        // the last good settings. The logger still throws it at startup.
        OptionsBuilder<ThrottledLoggingOptions> options = services.AddOptions<ThrottledLoggingOptions>()
            .Configure(target =>
            {
                try
                {
                    configuration.Bind(target);
                }
                catch (InvalidOperationException error)
                {
                    target.BindingError = error;
                }
            });
        services.AddSingleton<IOptionsChangeTokenSource<ThrottledLoggingOptions>>(new ConfigurationChangeTokenSource<ThrottledLoggingOptions>(configuration));

        if (configure is not null)
        {
            options.Configure(configure);
        }

        return services.AddCore();
    }

    private static IServiceCollection AddCore(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        // Built explicitly: OperationLogger offers several constructors, and the container cannot
        // choose between them on its own. The monitor is what lets configuration reload.
        services.TryAddSingleton(static provider => new OperationLogger(
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<IOptionsMonitor<ThrottledLoggingOptions>>(),
            provider.GetRequiredService<TimeProvider>()));

        // One instance behind both interfaces: the registry must see the operations the factory
        // created, and two singletons would each see only their own.
        services.TryAddSingleton<IOperationLogger>(static provider => provider.GetRequiredService<OperationLogger>());
        services.TryAddSingleton<IOperationRegistry>(static provider => provider.GetRequiredService<OperationLogger>());

        return services;
    }
}
