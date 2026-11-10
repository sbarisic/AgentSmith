using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentSmith.Desktop;

public partial class MainWindow
{
    private bool navigating;
    private void Navigate(string page)
    {
        if (WorkspaceTabs is null || navigating) return;
        navigating = true;
        try
        {
            HomeNavigation.IsChecked = page == "Home";
            LibraryNavigation.IsChecked = page == "Recordings";
            ConfigurationNavigation.IsChecked = page == "Configuration";
            LibraryPanel.Visibility = page == "Configuration" ? Visibility.Collapsed : Visibility.Visible;
            WorkspaceTabs.Visibility = page == "Recordings" ? Visibility.Collapsed : Visibility.Visible;
            LibraryColumn.Width = page == "Home" ? new GridLength(250) : page == "Recordings" ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            LibraryGap.Width = new GridLength(page == "Home" ? 12 : 0);
            WorkspaceColumn.Width = page == "Recordings" ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
            LibraryOpenHint.Visibility = page == "Recordings" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "Configuration") WorkspaceTabs.SelectedItem = ConfigurationTab;
            else if (page == "Home" && WorkspaceTabs.SelectedItem == ConfigurationTab) WorkspaceTabs.SelectedItem = RecordingTab;
        }
        finally { navigating = false; }
    }
    private void HomeNavigationClick(object sender, RoutedEventArgs e) => Navigate("Home");
    private void LibraryNavigationClick(object sender, RoutedEventArgs e) => Navigate("Recordings");
    private void ConfigurationNavigationClick(object sender, RoutedEventArgs e) => Navigate("Configuration");
    private void WorkspaceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, WorkspaceTabs)) Navigate(WorkspaceTabs.SelectedItem == ConfigurationTab ? "Configuration" : "Home");
    }
    private void OpenLibraryRecording(object sender, MouseButtonEventArgs e)
    { if (Selected is not null && LibraryNavigation.IsChecked == true) { Navigate("Home"); WorkspaceTabs.SelectedItem = RecordingTab; } }
    private void LibraryKeyDown(object sender, KeyEventArgs e)
    { if (e.Key == Key.Enter && Selected is not null) { Navigate("Home"); WorkspaceTabs.SelectedItem = RecordingTab; e.Handled = true; } }
    private void ExportMenuClick(object sender, RoutedEventArgs e)
    {
        ExportResultsMenu.IsEnabled = ExportButton.IsEnabled; ExportAudioMenu.IsEnabled = RawExportButton.IsEnabled;
        ExportMenuButton.ContextMenu.PlacementTarget = ExportMenuButton; ExportMenuButton.ContextMenu.IsOpen = true;
    }
}
