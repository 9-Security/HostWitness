using System;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using Microsoft.Win32;
using WinDFIR.Core.Settings;
using WinDFIR.UI.Services;
using WinDFIR.UI.ViewModels;

namespace WinDFIR.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow()
    {
        InitializeComponent();
        _viewModel = new SettingsViewModel();
        DataContext = _viewModel;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save(out var error))
        {
            if (Owner is MainWindow owner)
            {
                var settings = HostWitnessSettings.Load();
                owner.FontSize = HostWitnessSettings.GetEffectiveFontSize(settings);
            }

            MessageBox.Show("Settings saved. Please restart HostWitness to apply changes.",
                "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = true;
            Close();
            return;
        }

        MessageBox.Show(error ?? "Failed to save settings.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ApplyForensicStrict_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyForensicStrictProfile();
    }

    private void ApplyTriageFast_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.ApplyTriageFastProfile();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export settings profile",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            FileName = "hostwitness-profile.json"
        };
        if (dialog.ShowDialog() != true)
            return;
        if (_viewModel.ExportProfile(dialog.FileName, out var error))
        {
            MessageBox.Show("Profile exported.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(error ?? "Failed to export profile.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import settings profile",
            Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
            FileName = "hostwitness-profile.json"
        };
        if (dialog.ShowDialog() != true)
            return;
        if (_viewModel.ImportProfile(dialog.FileName, out var error))
        {
            MessageBox.Show("Profile imported. Click Save to persist.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        MessageBox.Show(error ?? "Failed to import profile.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RegistryHelpLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        ShellLaunchHelper.TryOpenRegistryHelpLink(e.Uri, AppDomain.CurrentDomain.BaseDirectory, this);
    }
}
