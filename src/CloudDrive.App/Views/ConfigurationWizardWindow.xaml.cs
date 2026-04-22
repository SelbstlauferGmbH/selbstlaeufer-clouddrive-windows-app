using System.ComponentModel;
using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.App.ViewModels;

namespace CloudDrive.App.Views;

public partial class ConfigurationWizardWindow : Window
{
    public ConfigurationWizardWindow(ConfigurationWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        ThemeManager.ApplyWindowTheme(this);
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationWizardViewModel vm)
            vm.PendingPassword = PasswordBox.Password;
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        (DataContext as ConfigurationWizardViewModel)?.MoveBack();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigurationWizardViewModel vm)
            return;

        if (vm.IsPreferencesStep)
        {
            vm.MoveNext();
            return;
        }

        if (await vm.FinishAsync())
        {
            DialogResult = true;
            Close();
        }
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigurationWizardViewModel vm)
            await vm.TestConnectionAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        (DataContext as IDisposable)?.Dispose();
        DataContext = null;
        base.OnClosing(e);
    }
}
