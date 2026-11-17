using System;
using System.Windows;
using Nebula.Services;

namespace Nebula.Views;

public partial class UpdateWindow : Window
{
    public event Action? InstallRequested;

    public UpdateWindow(UpdateManifest manifest, string currentVersion)
    {
        InitializeComponent();

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
    }

    public void ShowError(string message)
    {
        InstallButton.IsEnabled = true;
        LaterButton.IsEnabled = true;
        StatusText.Text = message;
    }

    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        SetBusy("Download in corso…");
        InstallRequested?.Invoke();
    }

    private void OnLaterClick(object sender, RoutedEventArgs e) => Close();
}
