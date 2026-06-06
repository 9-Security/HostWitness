using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;
using WinDFIR.Core.Snapshot;

namespace WinDFIR.UI.Views;

/// <summary>
/// Red/amber/green dashboard surfacing manifest-level collection-trust caveats (capped events,
/// dropped ETW, incomplete artifact copies, limited privilege, non-forensic registry, integrity)
/// so an analyst does not miss them when opening a snapshot. Display only — no data mutation.
/// </summary>
public partial class CollectionTrustWindow : Window
{
    private static readonly SolidColorBrush GreenBrush = Freeze(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush AmberBrush = Freeze(Color.FromRgb(0xE6, 0x8A, 0x00));
    private static readonly SolidColorBrush RedBrush = Freeze(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush GreyBrush = Freeze(Color.FromRgb(0x75, 0x75, 0x75));

    private readonly CollectionTrustReport _report;

    public sealed class SignalRow
    {
        public required string Name { get; init; }
        public required string Detail { get; init; }
        public required string BadgeText { get; init; }
        public required Brush BadgeBrush { get; init; }
    }

    public CollectionTrustWindow(CollectionTrustReport report)
    {
        _report = report ?? throw new ArgumentNullException(nameof(report));
        InitializeComponent();
        Render();
    }

    private void Render()
    {
        OverallBanner.Background = LevelBrush(_report.OverallLevel);
        OverallText.Text = $"Collection Trust: {LevelLabel(_report.OverallLevel)}";
        OverallSubText.Text = BuildOverallSubText();
        HeadlineText.Text = BuildHeadline();

        SignalsList.ItemsSource = _report.Signals.Select(ToRow).ToList();

        if (_report.KnownLimitations.Count > 0)
        {
            LimitationsList.ItemsSource = _report.KnownLimitations;
            LimitationsBorder.Visibility = Visibility.Visible;
        }
    }

    private string BuildOverallSubText()
    {
        if (!_report.ManifestPresent)
            return "manifest.json could not be read — trust cannot be assessed.";

        var red = _report.RedCount;
        var amber = _report.AmberCount;
        return _report.OverallLevel switch
        {
            CollectionTrustLevel.Green => "No collection-trust concerns detected in the manifest.",
            CollectionTrustLevel.Amber => $"{amber} caution signal(s). Review before relying on completeness.",
            _ => $"{red} critical and {amber} caution signal(s). Treat completeness/fidelity with care."
        };
    }

    private string BuildHeadline()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(_report.Hostname)) parts.Add($"Host: {_report.Hostname}");
        if (!string.IsNullOrWhiteSpace(_report.ToolVersion)) parts.Add($"Tool: {_report.ToolVersion}");
        if (!string.IsNullOrWhiteSpace(_report.CollectionTimeUtc)) parts.Add($"Collected: {_report.CollectionTimeUtc} UTC");
        if (_report.ExportedEventCount.HasValue)
        {
            var events = _report.SourceEventCount.HasValue && _report.SourceEventCount != _report.ExportedEventCount
                ? $"Events: {_report.ExportedEventCount:N0} of {_report.SourceEventCount:N0}"
                : $"Events: {_report.ExportedEventCount:N0}";
            parts.Add(events);
        }
        return parts.Count > 0 ? string.Join("    •    ", parts) : "(no headline metadata in manifest)";
    }

    private static SignalRow ToRow(CollectionTrustSignal signal)
    {
        var badge = signal.IsUnknown ? "UNKNOWN" : LevelLabel(signal.Level);
        var brush = signal.IsUnknown ? GreyBrush : LevelBrush(signal.Level);
        return new SignalRow
        {
            Name = signal.Name,
            Detail = signal.Detail,
            BadgeText = badge,
            BadgeBrush = brush
        };
    }

    private static Brush LevelBrush(CollectionTrustLevel level) => level switch
    {
        CollectionTrustLevel.Green => GreenBrush,
        CollectionTrustLevel.Amber => AmberBrush,
        _ => RedBrush
    };

    private static string LevelLabel(CollectionTrustLevel level) => level switch
    {
        CollectionTrustLevel.Green => "GREEN",
        CollectionTrustLevel.Amber => "AMBER",
        _ => "RED"
    };

    private string BuildClipboardSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Collection Trust: {LevelLabel(_report.OverallLevel)}");
        sb.AppendLine(BuildHeadline().Replace("    •    ", "\n"));
        sb.AppendLine();
        foreach (var s in _report.Signals)
        {
            var badge = s.IsUnknown ? "UNKNOWN" : LevelLabel(s.Level);
            sb.AppendLine($"[{badge}] {s.Name}: {s.Detail}");
        }
        if (_report.KnownLimitations.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Known limitations:");
            foreach (var l in _report.KnownLimitations)
                sb.AppendLine($"- {l}");
        }
        return sb.ToString();
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(BuildClipboardSummary());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"CollectionTrustWindow: copy failed: {ex.Message}");
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
