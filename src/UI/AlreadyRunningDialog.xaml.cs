using System.Windows;

namespace task_monitor
{
    public partial class AlreadyRunningDialog : Window
    {
        public AlreadyRunningDialog(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
            // i18n: MessageText is set above, so the walk below also covers the caller's
            // message when it matches a catalogue key.
            Loaded += (s, e) => Loc.ApplyOnce(this);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
