using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;

namespace WinDFIR.UI.Views;

public partial class LoadMftFromRawDialog : Window
{
    public IReadOnlyList<char> DriveLetters { get; private set; } = Array.Empty<char>();

    public char DriveLetter => DriveLetters.Count > 0 ? DriveLetters[0] : '\0';

    public LoadMftFromRawDialog()
    {
        InitializeComponent();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed)
                continue;

            var name = drive.Name;
            if (name.Length >= 2 && char.IsLetter(name[0]) && name[1] == ':')
                DriveLetterList.Items.Add(name.TrimEnd('\\'));
        }

        if (DriveLetterList.Items.Count > 0)
            DriveLetterList.SelectedIndex = 0;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var selected = new List<char>();
        foreach (var item in DriveLetterList.SelectedItems.OfType<string>())
        {
            if (!string.IsNullOrWhiteSpace(item) && item.Length >= 1 && char.IsLetter(item[0]))
                selected.Add(char.ToUpperInvariant(item[0]));
        }

        if (selected.Count == 0)
        {
            MessageBox.Show("Select at least one volume, for example C: or D:.", "Load MFT", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DriveLetters = selected.Distinct().ToArray();
        DialogResult = true;
        Close();
    }
}