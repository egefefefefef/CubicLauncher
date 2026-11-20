using Microsoft.Extensions.Logging;
using CubicLauncher.Infrastructure.Storage;

namespace CubicLauncher.Infrastructure.Logging;

public sealed class FileLoggerProvider(LauncherPaths paths) : ILoggerProvider
{
    private readonly object _gate = new();
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(paths.LogDirectory);
                File.AppendAllText(Path.Combine(paths.LogDirectory, $"launcher-{DateTime.UtcNow:yyyy-MM-dd}.log"), message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine(message);
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => (int)logLevel >= (int)LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
                provider.Write($"{DateTimeOffset.UtcNow:O} [{logLevel}] {category}: {formatter(state, exception)}\n{exception?.ToString() + (exception is null ? "" : "\n")}");
        }
    }
}
