using System.Collections.Concurrent;

namespace GemPrismUpscaler.Core.Diagnostics;

public enum AppErrorSeverity
{
    Warning,
    Error
}

public enum AppErrorKind
{
    /// <summary>A feature failed; the user must be told in normal mode.</summary>
    Functional,

    /// <summary>Noise/diagnostics; normal mode logs it only, developer mode surfaces it.</summary>
    Diagnostic
}

public sealed class AppError
{
    public DateTimeOffset Time { get; internal set; }
    public AppErrorSeverity Severity { get; internal init; }
    public AppErrorKind Kind { get; internal init; }
    public string Source { get; internal init; } = string.Empty;
    public string Message { get; internal init; } = string.Empty;
    public string? Detail { get; internal init; }
    public int Count { get; internal set; } = 1;

    public string DisplayText => Count > 1 ? $"{Message}（{Count} 次）" : Message;
}

/// <summary>
/// Central sink for user-visible/diagnostic errors. It never throws, keeps a
/// bounded recent list, and coalesces repeated messages so a validation-layer
/// flood cannot take over the UI.
/// </summary>
public static class ErrorBus
{
    private const int MaxRecent = 100;
    private static readonly object Gate = new();
    private static readonly List<AppError> RecentErrors = new();
    private static readonly Dictionary<string, AppError> Active = new(StringComparer.Ordinal);
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(10);

    public static event Action<AppError>? Reported;

    public static IReadOnlyList<AppError> Recent
    {
        get
        {
            lock (Gate) return RecentErrors.ToArray();
        }
    }

    public static void Functional(string source, string message, Exception? exception = null,
        string? context = null)
        => Report(new AppError
        {
            Time = DateTimeOffset.Now,
            Severity = AppErrorSeverity.Error,
            Kind = AppErrorKind.Functional,
            Source = source,
            Message = message,
            Detail = BuildDetail(exception, context)
        });

    public static void Error(string source, string message, Exception? exception = null,
        string? context = null)
        => Report(new AppError
        {
            Time = DateTimeOffset.Now,
            Severity = AppErrorSeverity.Error,
            Kind = AppErrorKind.Diagnostic,
            Source = source,
            Message = message,
            Detail = BuildDetail(exception, context)
        });

    public static void Warning(string source, string message, Exception? exception = null,
        string? context = null)
        => Report(new AppError
        {
            Time = DateTimeOffset.Now,
            Severity = AppErrorSeverity.Warning,
            Kind = AppErrorKind.Diagnostic,
            Source = source,
            Message = message,
            Detail = BuildDetail(exception, context)
        });

    public static void Clear()
    {
        lock (Gate)
        {
            RecentErrors.Clear();
            Active.Clear();
        }
    }

    private static void Report(AppError error)
    {
        AppError stored;
        bool isNew;
        lock (Gate)
        {
            string key = $"{error.Kind}|{error.Source}|{error.Message}";
            if (Active.TryGetValue(key, out var existing) &&
                error.Time - existing.Time <= CoalesceWindow)
            {
                existing.Count++;
                existing.Time = error.Time;
                stored = existing;
                isNew = false;
            }
            else
            {
                Active[key] = error;
                RecentErrors.Add(error);
                while (RecentErrors.Count > MaxRecent)
                    RecentErrors.RemoveAt(0);
                stored = error;
                isNew = true;
            }

            // Drop stale coalescing entries occasionally; the bounded recent
            // list is the real source of truth.
            if (Active.Count > MaxRecent * 2)
            {
                var stale = Active.Where(pair => error.Time - pair.Value.Time > CoalesceWindow)
                    .Select(pair => pair.Key).ToArray();
                foreach (string staleKey in stale) Active.Remove(staleKey);
            }
        }

        string logMessage = $"{stored.Source}: {stored.Message}";
        if (stored.Kind == AppErrorKind.Functional)
            Log.Error(logMessage, stored.Detail);
        else if (stored.Severity == AppErrorSeverity.Error)
            Log.Error(logMessage, stored.Detail);
        else
            Log.Warn($"{logMessage}{(string.IsNullOrWhiteSpace(stored.Detail) ? string.Empty : $" ({stored.Detail})")}");

        if (!isNew)
            return;

        try { Reported?.Invoke(stored); }
        catch
        {
            // UI subscribers must never take down the caller.
        }
    }

    private static string? BuildDetail(Exception? exception, string? context)
    {
        if (exception is null && string.IsNullOrWhiteSpace(context))
            return null;

        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(context)) parts.Add(context);
        if (exception is not null)
        {
            parts.Add(DiagnosticsRuntime.DeveloperMode
                ? exception.ToString()
                : $"{exception.GetType().Name}: {exception.Message}");
        }
        return string.Join('\n', parts);
    }
}
