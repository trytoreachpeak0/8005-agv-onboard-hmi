using System.IO;
using SQCD.Agv.Infrastructure;

namespace SQCD.Agv.Wpf.ScreenStateOutput;

/// <summary>The screen-state output's parameters, resolved from <see cref="ScreenStateSettings"/> (onboard-hmi#315).</summary>
internal sealed record HmiScreenStateOutputOptions(
    string Directory,
    TimeSpan FlushInterval,
    TimeSpan HeartbeatInterval,
    long TimelineMaxFileBytes,
    int TimelineRetentionDays)
{
    /// <summary>
    /// The directory defaults to the technical log's and is resolved the way the logger resolves its own: environment
    /// variables expanded, a relative path taken from the program directory. Same directory, same ACL (research 4.1).
    /// </summary>
    internal static HmiScreenStateOutputOptions From(ScreenStateSettings screenState, LogSettings logging)
    {
        ArgumentNullException.ThrowIfNull(screenState);
        ArgumentNullException.ThrowIfNull(logging);
        string directory = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            Environment.ExpandEnvironmentVariables(screenState.Directory ?? logging.Directory)));
        return new HmiScreenStateOutputOptions(
            directory,
            TimeSpan.FromMilliseconds(screenState.FlushIntervalMs),
            TimeSpan.FromMilliseconds(screenState.HeartbeatIntervalMs),
            screenState.TimelineMaxFileBytes,
            screenState.TimelineRetentionDays);
    }
}
