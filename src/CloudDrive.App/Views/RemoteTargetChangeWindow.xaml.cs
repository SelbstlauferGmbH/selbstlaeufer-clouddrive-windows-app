using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.Core.Localization;

namespace CloudDrive.App.Views;

public enum RemoteTargetChangeDecision
{
    Cancel,
    KeepLocalState,
    ResetLocalState
}

public partial class RemoteTargetChangeWindow : Window
{
    public RemoteTargetChangeWindow(string oldTarget, string newTarget)
    {
        InitializeComponent();
        ThemeManager.ApplyWindowTheme(this);

        var localizer = AppLocalizer.Instance;
        Title = localizer.GetString("App_Name");
        TitleText.Text = localizer.GetString("RemoteTargetChange_Heading");
        SummaryText.Text = localizer.GetString("RemoteTargetChange_Summary");
        OldTargetLabel.Text = localizer.GetString("RemoteTargetChange_OldTarget");
        NewTargetLabel.Text = localizer.GetString("RemoteTargetChange_NewTarget");
        OldTargetText.Text = oldTarget;
        NewTargetText.Text = newTarget;
        WarningText.Text = localizer.GetString("RemoteTargetChange_Warning");
        CancelButton.Content = localizer.GetString("Common_Cancel");
        KeepButton.Content = localizer.GetString("RemoteTargetChange_KeepButton");
        ResetButton.Content = localizer.GetString("RemoteTargetChange_ResetButton");
    }

    public RemoteTargetChangeDecision Decision { get; private set; } = RemoteTargetChangeDecision.Cancel;

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteTargetChangeDecision.Cancel;
        DialogResult = false;
    }

    private void KeepButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteTargetChangeDecision.KeepLocalState;
        DialogResult = true;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteTargetChangeDecision.ResetLocalState;
        DialogResult = true;
    }
}
