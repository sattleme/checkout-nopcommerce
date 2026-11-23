using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NopLogger = Nop.Services.Logging.ILogger;

namespace Nop.Plugin.Payments.Sattleme.Services;

/// <summary>
/// The SDK's warnings (a rate source that failed, a record of a newer format met…), written to
/// nopCommerce's log (System → Log). The SDK never gives its logger a secret: neither the key nor
/// anything derived from it. A warning that cannot be written is dropped: logging never fails a
/// payment.
/// </summary>
public partial class SdkLogger : ILogger
{
    #region Fields

    private readonly IServiceScopeFactory _scopes;

    #endregion

    #region Ctor

    /// <summary>A logger writing through nopCommerce's log, in a scope of its own.</summary>
    public SdkLogger(IServiceScopeFactory scopes)
    {
        _scopes = scopes;
    }

    #endregion

    #region Methods

    /// <inheritdoc/>
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        return null;
    }

    /// <inheritdoc/>
    public bool IsEnabled(LogLevel logLevel)
    {
        return logLevel >= LogLevel.Warning;
    }

    /// <inheritdoc/>
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        if (!IsEnabled(logLevel))
            return;
        var message = formatter(state, exception);
        _ = WriteAsync(message);
    }

    #endregion

    #region Utilities

    private async Task WriteAsync(string message)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var logger = scope.ServiceProvider.GetRequiredService<NopLogger>();
            await logger.WarningAsync(message);
        }
        catch
        {
            // The log is unavailable (the application stops, the database is down): dropped.
        }
    }

    #endregion
}