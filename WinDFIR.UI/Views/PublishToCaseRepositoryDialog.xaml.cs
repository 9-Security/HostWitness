using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace WinDFIR.UI.Views;

/// <summary>
/// Collects the inputs for publishing a finished snapshot bundle into a central case repository:
/// the bundle folder, the repository target (filesystem path or http(s):// intake URL), and an optional
/// intake token. Validation only checks the bundle folder exists and a target was given; the actual
/// integrity gate and transport selection happen in the sink.
/// </summary>
public partial class PublishToCaseRepositoryDialog : Window
{
    public string BundleFolder { get; private set; } = string.Empty;

    public string RepositoryTarget { get; private set; } = string.Empty;

    public string? Token { get; private set; }

    public PublishToCaseRepositoryDialog(string? initialBundleFolder = null)
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialBundleFolder))
            BundleFolderBox.Text = initialBundleFolder;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // Mirror the "Open Snapshot" picker: select timeline.json, then use its containing folder.
        var dialog = new OpenFileDialog
        {
            Title = "Select snapshot folder (choose timeline.json in the bundle folder)",
            Filter = "timeline.json|timeline.json|All files (*.*)|*.*",
            FileName = "timeline.json",
            CheckFileExists = false
        };

        if (dialog.ShowDialog() == true)
        {
            var folder = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrEmpty(folder))
                BundleFolderBox.Text = folder;
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var bundle = BundleFolderBox.Text?.Trim() ?? string.Empty;
        var target = RepositoryBox.Text?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(bundle) || !Directory.Exists(bundle))
        {
            MessageBox.Show(
                "Choose the snapshot bundle folder to publish (the folder that contains timeline.json).",
                "Publish Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrEmpty(target))
        {
            MessageBox.Show(
                "Enter a repository target: a folder / UNC share, or an http(s):// intake URL.",
                "Publish Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        BundleFolder = bundle;
        RepositoryTarget = target;
        var token = TokenBox.Password;
        Token = string.IsNullOrEmpty(token) ? null : token;

        DialogResult = true;
        Close();
    }
}
