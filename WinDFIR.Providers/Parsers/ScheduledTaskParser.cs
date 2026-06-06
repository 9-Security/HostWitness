using System.Globalization;
using System.Xml.Linq;

namespace WinDFIR.Providers.Parsers;

/// <summary>A single trigger on a scheduled task (TimeTrigger, LogonTrigger, BootTrigger, etc.).</summary>
public sealed class ScheduledTaskTrigger
{
    public string Type { get; init; } = "";
    public bool? Enabled { get; init; }
    public string? StartBoundary { get; init; }
    public DateTime? StartBoundaryUtc { get; init; }
}

/// <summary>A single action on a scheduled task (an Exec command or a ComHandler).</summary>
public sealed class ScheduledTaskAction
{
    public string Type { get; init; } = "";
    public string? Command { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? ComClassId { get; init; }
}

/// <summary>Parsed representation of a Windows Task Scheduler XML definition.</summary>
public sealed class ScheduledTaskRecord
{
    public string TaskName { get; init; } = "";
    public string? SourceFilePath { get; init; }

    public string? Author { get; init; }
    public string? Description { get; init; }
    public string? Uri { get; init; }
    public string? RegistrationDateRaw { get; init; }
    public DateTime? RegistrationDateUtc { get; init; }

    public string? PrincipalUserId { get; init; }
    public string? PrincipalGroupId { get; init; }
    public string? RunLevel { get; init; }
    public string? LogonType { get; init; }

    public bool? Enabled { get; init; }
    public bool? Hidden { get; init; }

    public IReadOnlyList<ScheduledTaskTrigger> Triggers { get; init; } = Array.Empty<ScheduledTaskTrigger>();
    public IReadOnlyList<ScheduledTaskAction> Actions { get; init; } = Array.Empty<ScheduledTaskAction>();

    /// <summary>First Exec action's command, if any — the executable the task launches.</summary>
    public ScheduledTaskAction? PrimaryExec =>
        Actions.FirstOrDefault(a => string.Equals(a.Type, "Exec", StringComparison.OrdinalIgnoreCase)
                                    && !string.IsNullOrWhiteSpace(a.Command));
}

/// <summary>
/// Parses Windows Task Scheduler XML (the files under <c>%WinDir%\System32\Tasks</c>).
/// Namespace-agnostic (matches by local element name) and tolerant of missing sections.
/// Never throws on malformed input — returns null instead.
/// </summary>
public static class ScheduledTaskParser
{
    /// <param name="taskNameOverride">
    /// Canonical task name; when null it is derived from the path's <c>Tasks</c> segment. Callers that
    /// know the Tasks root (e.g. <see cref="ScheduledTaskProvider"/>) should pass a root-relative name.
    /// </param>
    public static ScheduledTaskRecord? Parse(string filePath, string? taskNameOverride = null)
    {
        try
        {
            // Load from the byte stream so the BOM / declared encoding (real Task files are UTF-16)
            // is honored by the reader. XDocument.Parse on a decoded string would throw on an
            // encoding="utf-16" declaration.
            using var fs = File.OpenRead(filePath);
            var doc = XDocument.Load(fs);
            return FromDocument(doc, taskNameOverride ?? DeriveTaskNameFromPath(filePath), filePath);
        }
        catch
        {
            return null;
        }
    }

    public static ScheduledTaskRecord? ParseXml(string xml, string taskName, string? sourceFilePath = null)
    {
        if (string.IsNullOrWhiteSpace(xml))
            return null;

        XDocument doc;
        try
        {
            // Strip any XML declaration: a decoded string with encoding="utf-16" (or any non-matching
            // encoding) makes XDocument.Parse throw. The declaration is irrelevant once decoded.
            doc = XDocument.Parse(StripXmlDeclaration(xml));
        }
        catch
        {
            return null;
        }

        return FromDocument(doc, taskName, sourceFilePath);
    }

    private static ScheduledTaskRecord? FromDocument(XDocument doc, string taskName, string? sourceFilePath)
    {
        var root = doc.Root;
        if (root is null || !string.Equals(root.Name.LocalName, "Task", StringComparison.OrdinalIgnoreCase))
            return null;

        var registrationInfo = Child(root, "RegistrationInfo");
        var registrationDateRaw = ChildValue(registrationInfo, "Date");

        var principal = Child(Child(root, "Principals"), "Principal");

        var triggers = ParseTriggers(Child(root, "Triggers"));
        var actions = ParseActions(Child(root, "Actions"));

        var settings = Child(root, "Settings");

        return new ScheduledTaskRecord
        {
            TaskName = taskName,
            SourceFilePath = sourceFilePath,
            Author = ChildValue(registrationInfo, "Author"),
            Description = ChildValue(registrationInfo, "Description"),
            Uri = ChildValue(registrationInfo, "URI"),
            RegistrationDateRaw = registrationDateRaw,
            RegistrationDateUtc = ParseDate(registrationDateRaw),
            PrincipalUserId = ChildValue(principal, "UserId"),
            PrincipalGroupId = ChildValue(principal, "GroupId"),
            RunLevel = ChildValue(principal, "RunLevel"),
            LogonType = ChildValue(principal, "LogonType"),
            Enabled = ParseBool(ChildValue(settings, "Enabled")),
            Hidden = ParseBool(ChildValue(settings, "Hidden")),
            Triggers = triggers,
            Actions = actions
        };
    }

    private static List<ScheduledTaskTrigger> ParseTriggers(XElement? triggersElement)
    {
        var result = new List<ScheduledTaskTrigger>();
        if (triggersElement is null)
            return result;

        foreach (var trigger in triggersElement.Elements())
        {
            var startBoundary = ChildValue(trigger, "StartBoundary");
            result.Add(new ScheduledTaskTrigger
            {
                Type = trigger.Name.LocalName,
                Enabled = ParseBool(ChildValue(trigger, "Enabled")),
                StartBoundary = startBoundary,
                StartBoundaryUtc = ParseDate(startBoundary)
            });
        }

        return result;
    }

    private static List<ScheduledTaskAction> ParseActions(XElement? actionsElement)
    {
        var result = new List<ScheduledTaskAction>();
        if (actionsElement is null)
            return result;

        foreach (var action in actionsElement.Elements())
        {
            var type = action.Name.LocalName;
            if (string.Equals(type, "Exec", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ScheduledTaskAction
                {
                    Type = "Exec",
                    Command = ChildValue(action, "Command"),
                    Arguments = ChildValue(action, "Arguments"),
                    WorkingDirectory = ChildValue(action, "WorkingDirectory")
                });
            }
            else if (string.Equals(type, "ComHandler", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(new ScheduledTaskAction
                {
                    Type = "ComHandler",
                    ComClassId = ChildValue(action, "ClassId")
                });
            }
            else
            {
                result.Add(new ScheduledTaskAction { Type = type });
            }
        }

        return result;
    }

    /// <summary>
    /// Derives the canonical task name (e.g. <c>\Microsoft\Windows\Defrag\ScheduledDefrag</c>) from a file path
    /// under a <c>...\Tasks</c> root. Falls back to the file name when the path is not under a Tasks folder.
    /// </summary>
    internal static string DeriveTaskNameFromPath(string filePath)
    {
        try
        {
            var full = Path.GetFullPath(filePath);
            var parts = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var tasksIndex = Array.FindLastIndex(parts, p => string.Equals(p, "Tasks", StringComparison.OrdinalIgnoreCase));
            if (tasksIndex >= 0 && tasksIndex < parts.Length - 1)
                return "\\" + string.Join('\\', parts.Skip(tasksIndex + 1));
            return "\\" + Path.GetFileName(full);
        }
        catch
        {
            return "\\" + Path.GetFileName(filePath);
        }
    }

    private static string StripXmlDeclaration(string xml)
    {
        var trimmed = xml.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        var end = trimmed.IndexOf("?>", StringComparison.Ordinal);
        return end < 0 ? trimmed : trimmed[(end + 2)..];
    }

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(e => string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static string? ChildValue(XElement? parent, string localName)
    {
        var value = Child(parent, localName)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool? ParseBool(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        if (bool.TryParse(raw, out var b))
            return b;
        return raw.Trim() switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };
    }

    private static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        // Task boundaries are typically local time without a zone (e.g. 2026-01-16T09:00:00);
        // AssumeLocal converts those correctly, while explicit Z/offset values are honored as-is.
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var dt))
            return dt.ToUniversalTime();
        return null;
    }
}
