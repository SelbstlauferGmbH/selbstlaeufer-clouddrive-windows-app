using System.Windows;
using CloudDrive.App.Services;
using CloudDrive.Core.Localization;

namespace CloudDrive.App.Views;

public enum RemoteDeleteDecision
{
    None,
    Reupload,
    DeleteLocal
}

public sealed class RemoteDeleteDecisionItem
{
    public required long ProblemId { get; init; }
    public required string LocalPath { get; init; }
    public required string RemotePath { get; init; }
    public required string DisplayName { get; init; }
    public required string Location { get; init; }
    public required string RemotePathDisplay { get; init; }
}

public partial class RemoteDeleteDecisionWindow : Window
{
    public RemoteDeleteDecisionWindow(IReadOnlyList<RemoteDeleteDecisionItem> items)
    {
        InitializeComponent();
        ThemeManager.ApplyWindowTheme(this);

        Items = items;
        ItemsList.ItemsSource = Items;

        var localizer = AppLocalizer.Instance;
        Title = localizer.GetString("App_Name");
        TitleText.Text = items.Count == 1
            ? localizer.Format("RemoteDeleteDecision_Heading_One", items[0].DisplayName)
            : localizer.Format("RemoteDeleteDecision_Heading_Many", items.Count);
        SummaryText.Text = items.Count == 1
            ? localizer.GetString("RemoteDeleteDecision_Summary_One")
            : localizer.GetString("RemoteDeleteDecision_Summary_Many");
        ReuploadButton.Content = localizer.GetString("Problem_Action_ReuploadLocal");
        DeleteButton.Content = localizer.GetString("Problem_Action_DeleteLocal");
        LaterButton.Content = localizer.GetString("RemoteDeleteDecision_DecideLater");
    }

    public IReadOnlyList<RemoteDeleteDecisionItem> Items { get; }

    public RemoteDeleteDecision Decision { get; private set; } = RemoteDeleteDecision.None;

    private void ReuploadButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteDeleteDecision.Reupload;
        DialogResult = true;
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteDeleteDecision.DeleteLocal;
        DialogResult = true;
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        Decision = RemoteDeleteDecision.None;
        DialogResult = false;
    }
}
