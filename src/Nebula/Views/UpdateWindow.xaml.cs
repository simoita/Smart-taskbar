using System;
using System.Globalization;
using System.Windows;
using Nebula.Services;
using Nebula.Utilities;

namespace Nebula.Views;

public partial class UpdateWindow : Window
{
    public event Action? InstallRequested;

    public UpdateWindow(UpdateManifest manifest, string currentVersion)
    {
        InitializeComponent();
        TitleBarTheme.Apply(this);

        VersionText.Text = $"Nebula {manifest.Version} è pronta per l'installazione.";
        CurrentText.Text = $"Versione installata: {currentVersion}";
        NotesText.Text = string.IsNullOrWhiteSpace(manifest.Notes)
            ? "Questa versione contiene miglioramenti e correzioni."
            : manifest.Notes;
    }

    public void SetBusy(string message)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        StatusText.Text = message;

        ProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        ProgressText.Text = "Preparo il download…";
        ProgressPercent.Text = "";
    }

    /// <summary>
    /// Shows the real download state: completed bytes, total size, speed and the
    /// bar filling with the percentage of the package received.
    /// </summary>
    public void SetProgress(UpdateProgress progress)
    {
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = progress.Describe();

        double? percent = progress.Percent;
        if (percent is double value)
        {
            DownloadProgressBar.Value = value;
            ProgressPercent.Text = value.ToString("0", CultureInfo.CurrentCulture) + "%";
        }
        else
        {
            DownloadProgressBar.Value = 0;
            ProgressPercent.Text = "";
        }
    }

    public void ShowError(string message)
    {
        InstallButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        StatusText.Text = message;
        ProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        SetBusy("Download in corso…");
        InstallRequested?.Invoke();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();
}
