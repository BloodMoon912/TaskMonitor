using System.Windows;

namespace task_monitor
{
    public partial class StartupGuideWindow : Window
    {
        public bool UseFloatingMode => FloatingOption.IsChecked == true;
        public bool OverlayOnLeft => TrayOption.IsChecked != true;
        public bool OverlaySnapToStart => StartOption.IsChecked == true;

        public StartupGuideWindow(bool snapToStartSupported)
        {
            InitializeComponent();
            // i18n: Window.Title and InfoBar.Title/Message are plain string properties — not
            // TextBlock.Text/Content/Header — so ApplyOnce's tree walk cannot reach them. Set
            // them through the catalogue here; the Loaded hook translates everything else.
            Title = Loc.T(Title);
            TaskbarWarning.Title = Loc.T(TaskbarWarning.Title);
            TaskbarWarning.Message = Loc.T(TaskbarWarning.Message);
            StartOption.IsEnabled = snapToStartSupported;
            FloatingOption.Checked += ModeOption_Checked;
            TaskbarOption.Checked += ModeOption_Checked;
            Loaded += (s, e) => Loc.ApplyOnce(this);
        }

        private void ModeOption_Checked(object sender, RoutedEventArgs e)
        {
            // Loc.T: these are set AFTER Loaded, so ApplyOnce has already run over the tree —
            // an unwrapped literal would revert to Simplified Chinese on the first click.
            PrimaryButton.Content = Loc.T(UseFloatingMode ? "完成" : "下一步");
            TaskbarWarning.Visibility = UseFloatingMode ? Visibility.Collapsed : Visibility.Visible;
        }

        private void Primary_Click(object sender, RoutedEventArgs e)
        {
            if (UseFloatingMode || PlacementPage.Visibility == Visibility.Visible)
            {
                DialogResult = true;
                return;
            }

            ModePage.Visibility = Visibility.Collapsed;
            PlacementPage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Visible;
            PrimaryButton.Content = Loc.T("完成");
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            PlacementPage.Visibility = Visibility.Collapsed;
            ModePage.Visibility = Visibility.Visible;
            BackButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Content = Loc.T(UseFloatingMode ? "完成" : "下一步");
        }
    }
}
