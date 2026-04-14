using WinDFIR.Core.Entities;

namespace WinDFIR.Core.Normalization;

/// <summary>
/// Normalizes ActivityEvent fields to match the specification.
/// </summary>
public static class ActivityEventNormalizer
{
    public static ActivityEvent Normalize(ActivityEvent activityEvent)
    {
        if (activityEvent is null)
            throw new ArgumentNullException(nameof(activityEvent));

        var normalizedAction = NormalizeAction(activityEvent.Action);
        var fields = activityEvent.Fields != null
            ? new Dictionary<string, object>(activityEvent.Fields)
            : new Dictionary<string, object>();

        if (!string.Equals(normalizedAction, activityEvent.Action, StringComparison.Ordinal))
        {
            if (!fields.ContainsKey("OriginalAction"))
                fields["OriginalAction"] = activityEvent.Action;
        }

        return activityEvent with { Action = normalizedAction, Fields = fields };
    }

    public static string NormalizeAction(string action)
    {
        if (string.IsNullOrWhiteSpace(action))
            return "Query";

        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
                return "Start";
            case "stop":
                return "Stop";
            case "open":
                return "Open";
            case "write":
                return "Write";
            case "connect":
                return "Connect";
            case "query":
                return "Query";
            case "create":
            case "set":
                return "Write";
            case "execute":
            case "launch":
            case "run":
            case "visit":
                return "Open";
            case "listen":
            case "connected":
                return "Connect";
            case "disconnect":
            case "disconnected":
            case "close":
                return "Stop";
            case "failed":
            case "failure":
            case "denied":
                return "Query";
            default:
                return "Query";
        }
    }
}
