using System.Windows;

namespace ClassroomNoiseMonitor
{
    public partial class SettingsWindow : Window
    {
        public double Threshold { get; private set; }
        public int Reward { get; private set; }
        public int Penalty { get; private set; }
        public double Sensitivity { get; private set; }
        public int SilenceDuration { get; private set; }
        public int CelebrationDuration { get; private set; }
        public bool SoundAlertEnabled { get; private set; }

        public SettingsWindow(double threshold, int reward, int penalty, double sensitivity, int silenceDuration, int celebrationDuration, bool soundAlertEnabled)
        {
            InitializeComponent();

            ThresholdBox.Text = threshold.ToString();
            RewardBox.Text = reward.ToString();
            PenaltyBox.Text = penalty.ToString();
            SensitivityBox.Text = sensitivity.ToString();
            SilenceDurationBox.Text = silenceDuration.ToString();
            CelebrationDurationBox.Text = celebrationDuration.ToString();
            SoundAlertCheckBox.IsChecked = soundAlertEnabled;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (double.TryParse(ThresholdBox.Text, out double t) &&
                int.TryParse(RewardBox.Text, out int r) &&
                int.TryParse(PenaltyBox.Text, out int p) &&
                double.TryParse(SensitivityBox.Text, out double s) &&
                int.TryParse(SilenceDurationBox.Text, out int sd) &&
                int.TryParse(CelebrationDurationBox.Text, out int cd))
            {
                Threshold = t;
                Reward = r;
                Penalty = p;
                Sensitivity = s;
                SilenceDuration = sd;
                CelebrationDuration = cd;
                SoundAlertEnabled = SoundAlertCheckBox.IsChecked ?? true;

                DialogResult = true;
            }
            else
            {
                MessageBox.Show("Invalid input values.");
            }
        }
    }
}