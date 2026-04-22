using System.Windows;
using CloudDrive.App.Services;

namespace CloudDrive.App.Views;

public partial class StaleFolderWarningWindow : Window
{
    public StaleFolderWarningWindow()
    {
        InitializeComponent();
        ThemeManager.ApplyWindowTheme(this);
    }
}
