using System.Windows;

namespace Nebula.Views;

public partial class UpdateFailureWindow : Window
{
    public UpdateFailureWindow(string details, string currentVersion)
    {
        InitializeComponent();
        VersionText.Text = $"Nebula ha mantenuto la versione {currentVersion} e le tue impostazioni.";
        DetailText.Text = details;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
