using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Nebula.Models;
using Nebula.ViewModels;

namespace Nebula.Views;

public partial class SystemIconsPage : UserControl
{
    private bool _loadedOnce;

    public SystemIconsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private SystemIconsViewModel? ViewModel => DataContext as SystemIconsViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce || ViewModel is null)
            return;
        _loadedOnce = true;
        ViewModel.Refresh();
    }

    private async void OnApplyAll(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SystemIconsViewModel vm)
            return;

        try
        {
            await vm.ApplyAllAsync(vm.SelectedStyle, vm.SelectedShape);
        }
        catch (Exception ex)
        {
            vm.SetStatus("Applicazione non riuscita: " + ex.Message);
        }
    }

    private async void OnRestoreAll(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SystemIconsViewModel vm)
            return;

        try
        {
            await vm.RestoreAsync();
        }
        catch (Exception ex)
        {
            vm.SetStatus("Ripristino non riuscito: " + ex.Message);
        }
    }

    private async void OnPickItem(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SystemIconsViewModel vm ||
            sender is not Button { Tag: SystemIconItemViewModel item } ||
            Window.GetWindow(this) is not Window owner)
            return;

        var picker = new IconPickerWindow(item.Target, vm.Service, vm.SelectedStyle, vm.SelectedShape)
        {
            Owner = owner
        };
        if (picker.ShowDialog() != true || picker.Choice is null)
            return;

        try
        {
            await vm.ApplyTargetAsync(item.Target, picker.Choice);
        }
        catch (Exception ex)
        {
            vm.SetStatus("Icona non applicata: " + ex.Message);
        }
    }

    private async void OnRestoreItem(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SystemIconsViewModel vm ||
            sender is not Button { Tag: SystemIconItemViewModel item })
            return;

        try
        {
            await vm.RestoreAsync(item.Target);
        }
        catch (Exception ex)
        {
            vm.SetStatus("Ripristino non riuscito: " + ex.Message);
        }
    }

    private void OnOpenBackupFolder(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not SystemIconsViewModel vm)
            return;

        try
        {
            Directory.CreateDirectory(vm.Service.RootDirectory);
            Process.Start(new ProcessStartInfo(vm.Service.RootDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            vm.SetStatus("Cartella non apribile: " + ex.Message);
        }
    }
}
