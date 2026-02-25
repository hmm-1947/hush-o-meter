using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using NAudio.Wave;
using NAudio.Dsp;
using System.Windows.Controls;
using System.Windows.Media.Effects;
using System.IO;
using System.Text.Json;
using System.Media;

namespace ClassroomNoiseMonitor
{
    // Class to store settings and data
    public class AppData
    {
        public double NoiseThreshold { get; set; }
        public int RewardPoints { get; set; }
        public int PenaltyPoints { get; set; }
        public double Sensitivity { get; set; }
        public int SilenceDuration { get; set; }
        public int CelebrationDuration { get; set; }
        public int Points { get; set; }
        public bool SoundAlertEnabled { get; set; } = true; // Default: sound enabled
    }

    public partial class MainWindow : Window
    {
        private const string DATA_FILE = "classroom_data.json";

        private double noiseThreshold = 20; // Positive threshold (0-60 scale)
        private int rewardPoints = 1;
        private int penaltyPoints = 3;
        private double sensitivity = 50.0; // Adjust this to control bar heights
        private int silenceDuration = 10; // Seconds of silence required for reward
        private int celebrationDuration = 20; // Seconds of silence required for celebration
        private bool soundAlertEnabled = true; // Whether to play sound when noise exceeds threshold

        private int points = 0;
        private DateTime silenceStart;
        private bool isSilent = false;
        private DateTime lastCelebrationTime; // Track when last celebration was shown
        private DateTime lastSoundAlert = DateTime.MinValue; // Track when last sound was played
        private const int SOUND_ALERT_COOLDOWN_SECONDS = 5; // Don't spam the sound

        private WaveInEvent? waveIn;

        private const int FFT_SIZE = 1024;
        private Complex[] fftBuffer = new Complex[FFT_SIZE];
        private int fftPos = 0;

        // Smooth animation variables
        private double[] currentBarHeights = new double[60];
        private double[] targetBarHeights = new double[60];
        private double[] peakHeights = new double[60];
        private const double SMOOTHING_FACTOR = 0.3; // Higher = faster response
        private const double PEAK_FALL_SPEED = 2.0; // Pixels per frame

        public MainWindow()
        {
            InitializeComponent();
            LoadData(); // Load saved data before starting
            StartListening();
            
            // Add closing event handler
            this.Closing += MainWindow_Closing;
        }

        private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to exit?\n\nYour points and settings have been saved.",
                "Exit Hush-o-Meter",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true; // Cancel the closing
            }
        }

        private void StartListening()
        {
            waveIn = new WaveInEvent();
            waveIn.WaveFormat = new WaveFormat(44100, 16, 1);
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.StartRecording();
        }

        private void OnDataAvailable(object? sender, WaveInEventArgs e)
        {
            int bytesPerSample = 2;
            int sampleCount = e.BytesRecorded / bytesPerSample;
            double sum = 0;

            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                short sample = (short)((e.Buffer[i + 1] << 8) | e.Buffer[i]);
                float sample32 = sample / 32768f;

                sum += sample32 * sample32;

                fftBuffer[fftPos].X = sample32;
                fftBuffer[fftPos].Y = 0;
                fftPos++;

                if (fftPos >= FFT_SIZE)
                {
                    fftPos = 0;
                    FastFourierTransform.FFT(true, (int)Math.Log(FFT_SIZE, 2.0), fftBuffer);

                    Dispatcher.Invoke(() =>
                    {
                        DrawSpectrum();
                    });
                }
            }

            double rms = Math.Sqrt(sum / sampleCount);
            double decibels = 20 * Math.Log10(rms);
            
            // Convert to a positive scale where higher noise = higher number
            // Typical range is -60 (quiet) to 0 (loud), so we invert it
            double displayDecibels = Math.Max(0, 60 + decibels); // Now 0=quiet, 60=loud

            Dispatcher.Invoke(() =>
            {
                DbText.Text = $"{displayDecibels:F0}"; // Remove "dB:" prefix, show just the number
                
                // Update countdown timer
                if (isSilent)
                {
                    double timeSinceLastCelebration = (DateTime.Now - lastCelebrationTime).TotalSeconds;
                    double timeUntilNext = celebrationDuration - timeSinceLastCelebration;
                    if (timeUntilNext > 0)
                    {
                        CountdownText.Text = $"{timeUntilNext:F0}s";
                    }
                    else
                    {
                        CountdownText.Text = "Now!";
                    }
                }
                else
                {
                    CountdownText.Text = "--";
                }

                // Use displayDecibels (positive scale) instead of decibels (negative scale)
                if (displayDecibels > noiseThreshold)
                {
                    if (isSilent)
                    {
                        points -= penaltyPoints;
                        if (points < 0) points = 0;
                        PointsText.Text = $"{points}";
                        GlowPointsContainer(false); // Red glow when points decrease
                        SaveData(); // Save points immediately
                        
                        // Play alert sound (with cooldown to avoid spam)
                        PlayNoiseAlert();
                    }
                    isSilent = false;
                }
                else
                {
                    if (!isSilent)
                    {
                        silenceStart = DateTime.Now;
                        lastCelebrationTime = DateTime.Now; // Reset celebration timer
                        isSilent = true;
                    }
                    else
                    {
                        double silentSeconds = (DateTime.Now - silenceStart).TotalSeconds;
                        double timeSinceLastCelebration = (DateTime.Now - lastCelebrationTime).TotalSeconds;
                        
                        // Regular reward
                        if (silentSeconds >= silenceDuration)
                        {
                            points += rewardPoints;
                            PointsText.Text = $"{points}";
                            GlowPointsContainer(true); // Green glow when points increase
                            SaveData(); // Save points immediately
                            silenceStart = DateTime.Now;
                        }
                        
                        // Celebration trigger - repeats every celebrationDuration seconds
                        if (timeSinceLastCelebration >= celebrationDuration)
                        {
                            ShowCelebration();
                            lastCelebrationTime = DateTime.Now; // Reset timer for next celebration
                        }
                    }
                }
            });
        }

        private void DrawSpectrum()
        {
            SpectrumCanvas.Children.Clear();

            double width = SpectrumCanvas.ActualWidth;
            double height = SpectrumCanvas.ActualHeight;

            // Draw grid background
            DrawGrid(width, height);

            int barCount = 60; // More bars for better visualization
            double barWidth = width / barCount;

            for (int i = 0; i < barCount; i++)
            {
                int fftIndex = i * (FFT_SIZE / 2) / barCount;

                double magnitude = Math.Sqrt(
                    fftBuffer[fftIndex].X * fftBuffer[fftIndex].X +
                    fftBuffer[fftIndex].Y * fftBuffer[fftIndex].Y
                );

                // Calculate target bar height
                targetBarHeights[i] = magnitude * sensitivity * height;

                if (targetBarHeights[i] < 8)
                    targetBarHeights[i] = 8; // Minimum bar height

                if (targetBarHeights[i] > height)
                    targetBarHeights[i] = height;

                // Smooth interpolation - bars rise quickly, fall slowly
                if (targetBarHeights[i] > currentBarHeights[i])
                {
                    // Rise quickly
                    currentBarHeights[i] += (targetBarHeights[i] - currentBarHeights[i]) * SMOOTHING_FACTOR;
                }
                else
                {
                    // Fall slowly
                    currentBarHeights[i] += (targetBarHeights[i] - currentBarHeights[i]) * (SMOOTHING_FACTOR * 0.3);
                }

                double barHeight = currentBarHeights[i];

                // Update peak hold
                if (barHeight > peakHeights[i])
                {
                    peakHeights[i] = barHeight;
                }
                else
                {
                    // Peak falls slowly
                    peakHeights[i] -= PEAK_FALL_SPEED;
                    if (peakHeights[i] < barHeight)
                        peakHeights[i] = barHeight;
                }

                // Draw stacked blocks instead of solid bar
                DrawStackedBlocks(i * barWidth, barWidth, barHeight, height);

                // Draw peak hold indicator (small rectangle on top)
                Rectangle peakRect = new Rectangle
                {
                    Width = barWidth - 1,
                    Height = 3, // Height of peak indicator
                    Fill = Brushes.White
                };

                Canvas.SetLeft(peakRect, i * barWidth);
                Canvas.SetTop(peakRect, height - peakHeights[i]);

                SpectrumCanvas.Children.Add(peakRect);
            }
        }

        private void DrawGrid(double width, double height)
        {
            double blockHeight = 8; // Height of each individual block
            double blockGap = 2; // Gap between blocks
            double combinedBlockHeight = blockHeight + blockGap; // 10 pixels total

            // Draw horizontal grid lines - aligned with blocks
            int horizontalLines = (int)(height / combinedBlockHeight);
            for (int i = 0; i <= horizontalLines; i++)
            {
                double y = i * combinedBlockHeight;
                
                System.Windows.Shapes.Line line = new System.Windows.Shapes.Line
                {
                    X1 = 0,
                    Y1 = y,
                    X2 = width,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), // Semi-transparent white
                    StrokeThickness = 1
                };
                
                SpectrumCanvas.Children.Add(line);
            }

            // Draw vertical grid lines - aligned with bars
            int barCount = 60;
            double barWidth = width / barCount;
            for (int i = 0; i <= barCount; i++)
            {
                double x = i * barWidth;
                
                System.Windows.Shapes.Line line = new System.Windows.Shapes.Line
                {
                    X1 = x,
                    Y1 = 0,
                    X2 = x,
                    Y2 = height,
                    Stroke = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), // Semi-transparent white
                    StrokeThickness = 1
                };
                
                SpectrumCanvas.Children.Add(line);
            }
        }

        private void DrawStackedBlocks(double xPosition, double barWidth, double totalHeight, double canvasHeight)
        {
            double blockHeight = 8; // Height of each individual block
            double blockGap = 2; // Gap between blocks
            double combinedBlockHeight = blockHeight + blockGap;

            int numberOfBlocks = (int)(totalHeight / combinedBlockHeight);

            for (int j = 0; j < numberOfBlocks; j++)
            {
                double blockY = canvasHeight - (j + 1) * combinedBlockHeight;
                
                // Calculate color based on height position
                double heightRatio = (j * combinedBlockHeight) / canvasHeight;
                
                Rectangle block = new Rectangle
                {
                    Width = barWidth - 1,
                    Height = blockHeight,
                    Fill = GetBlockColor(heightRatio)
                };

                Canvas.SetLeft(block, xPosition);
                Canvas.SetTop(block, blockY);

                SpectrumCanvas.Children.Add(block);
            }
        }

        private Brush GetBlockColor(double heightRatio)
        {
            // Green at bottom, yellow in middle, red at top
            if (heightRatio < 0.5)
            {
                return new SolidColorBrush(Color.FromRgb(0, 255, 0)); // Green
            }
            else if (heightRatio < 0.7)
            {
                return new SolidColorBrush(Color.FromRgb(200, 255, 0)); // Yellow-green
            }
            else if (heightRatio < 0.85)
            {
                return new SolidColorBrush(Color.FromRgb(255, 255, 0)); // Yellow
            }
            else if (heightRatio < 0.95)
            {
                return new SolidColorBrush(Color.FromRgb(255, 150, 0)); // Orange
            }
            else
            {
                return new SolidColorBrush(Color.FromRgb(255, 0, 0)); // Red
            }
        }

        private Brush CreateGradientBrush(double barHeight, double maxHeight)
        {
            LinearGradientBrush brush = new LinearGradientBrush();
            brush.StartPoint = new Point(0, 1); // Bottom
            brush.EndPoint = new Point(0, 0);   // Top

            double ratio = barHeight / maxHeight;

            // Green at bottom
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(0, 255, 0), 0.0));
            
            // Yellow-green transition
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(150, 255, 0), 0.4));
            
            // Yellow
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 0), 0.6));
            
            // Orange
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 150, 0), 0.8));
            
            // Red at top
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 0, 0), 1.0));

            return brush;
        }

        private void ShowCelebration()
        {
            // Create celebration overlay
            Canvas celebrationCanvas = new Canvas
            {
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), // Semi-transparent black
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Width = SpectrumCanvas.ActualWidth,
                Height = SpectrumCanvas.ActualHeight
            };

            // Add encouraging message
            string[] messages = { "🎉 AMAZING! 🎉", "⭐ GREAT JOB! ⭐", "🌟 EXCELLENT! 🌟", "👏 WONDERFUL! 👏", "🎊 FANTASTIC! 🎊" };
            Random rand = new Random();
            
            TextBlock messageText = new TextBlock
            {
                Text = messages[rand.Next(messages.Length)],
                FontSize = 80,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Yellow,
                    BlurRadius = 20,
                    ShadowDepth = 0
                }
            };

            Canvas.SetLeft(messageText, (celebrationCanvas.Width - 600) / 2);
            Canvas.SetTop(messageText, celebrationCanvas.Height / 2 - 50);
            celebrationCanvas.Children.Add(messageText);

            // Create confetti particles
            for (int i = 0; i < 100; i++)
            {
                CreateConfetti(celebrationCanvas, rand);
            }

            // Add to main grid
            ((Grid)SpectrumCanvas.Parent).Children.Add(celebrationCanvas);

            // Remove after 3 seconds
            System.Windows.Threading.DispatcherTimer timer = new System.Windows.Threading.DispatcherTimer();
            timer.Interval = TimeSpan.FromSeconds(3);
            timer.Tick += (s, e) =>
            {
                ((Grid)SpectrumCanvas.Parent).Children.Remove(celebrationCanvas);
                timer.Stop();
            };
            timer.Start();
        }

        private void CreateConfetti(Canvas canvas, Random rand)
        {
            string[] emojis = { "⭐", "✨", "🌟", "💫", "🎈", "🎉", "🎊", "👏", "😊", "🏆" };
            Color[] colors = { Colors.Red, Colors.Orange, Colors.Yellow, Colors.Lime, Colors.Cyan, Colors.Magenta, Colors.Pink };

            // Randomly choose between emoji or colored rectangle
            bool useEmoji = rand.Next(2) == 0;

            if (useEmoji)
            {
                TextBlock emoji = new TextBlock
                {
                    Text = emojis[rand.Next(emojis.Length)],
                    FontSize = rand.Next(20, 50),
                    Opacity = 0.9
                };

                double startX = rand.Next((int)canvas.Width);
                double startY = rand.Next((int)canvas.Height);

                Canvas.SetLeft(emoji, startX);
                Canvas.SetTop(emoji, startY);
                canvas.Children.Add(emoji);

                AnimateConfetti(emoji, startX, startY, canvas.Height, rand);
            }
            else
            {
                Rectangle rect = new Rectangle
                {
                    Width = rand.Next(10, 20),
                    Height = rand.Next(10, 20),
                    Fill = new SolidColorBrush(colors[rand.Next(colors.Length)]),
                    Opacity = 0.9
                };

                double startX = rand.Next((int)canvas.Width);
                double startY = rand.Next((int)canvas.Height);

                Canvas.SetLeft(rect, startX);
                Canvas.SetTop(rect, startY);
                canvas.Children.Add(rect);

                AnimateConfetti(rect, startX, startY, canvas.Height, rand);
            }
        }

        private void AnimateConfetti(UIElement element, double startX, double startY, double canvasHeight, Random rand)
        {
            System.Windows.Media.Animation.DoubleAnimation fallAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = startY,
                To = canvasHeight + 100,
                Duration = TimeSpan.FromSeconds(rand.Next(2, 4)),
                EasingFunction = new System.Windows.Media.Animation.QuadraticEase()
            };

            System.Windows.Media.Animation.DoubleAnimation sideAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = startX,
                To = startX + rand.Next(-200, 200),
                Duration = TimeSpan.FromSeconds(rand.Next(2, 4)),
                EasingFunction = new System.Windows.Media.Animation.SineEase()
            };

            System.Windows.Media.Animation.DoubleAnimation fadeAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0.9,
                To = 0,
                Duration = TimeSpan.FromSeconds(2.5)
            };

            element.BeginAnimation(Canvas.TopProperty, fallAnimation);
            element.BeginAnimation(Canvas.LeftProperty, sideAnimation);
            element.BeginAnimation(UIElement.OpacityProperty, fadeAnimation);
        }

        private void GlowPointsContainer(bool isPositive = true)
        {
            // Choose color based on whether points increased or decreased
            Color glowColor = isPositive ? Color.FromRgb(76, 175, 80) : Color.FromRgb(244, 67, 54); // Green or Red
            
            // Create glow animation for the points border
            var glowAnimation = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 40,
                To = 80,
                Duration = TimeSpan.FromMilliseconds(200),
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2) // Pulse twice
            };

            // Create color animation for the glow effect
            var colorAnimation = new System.Windows.Media.Animation.ColorAnimation
            {
                To = glowColor,
                Duration = TimeSpan.FromMilliseconds(100),
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
            };

            // Apply to the DropShadowEffect's BlurRadius and Color
            if (PointsBorder.Effect is DropShadowEffect dropShadow)
            {
                dropShadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowAnimation);
                dropShadow.BeginAnimation(DropShadowEffect.ColorProperty, colorAnimation);
            }

            // Also animate the border thickness for extra emphasis
            var scaleAnimation = new System.Windows.Media.Animation.ThicknessAnimation
            {
                From = new Thickness(5),
                To = new Thickness(8),
                Duration = TimeSpan.FromMilliseconds(200),
                AutoReverse = true,
                RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
            };

            PointsBorder.BeginAnimation(Border.BorderThicknessProperty, scaleAnimation);
        }

        private void PlayNoiseAlert()
        {
            // Check if sound alerts are enabled
            if (!soundAlertEnabled)
            {
                System.Diagnostics.Debug.WriteLine("Sound alert is disabled in settings");
                return;
            }

            // Check cooldown to avoid playing sound too frequently
            if ((DateTime.Now - lastSoundAlert).TotalSeconds < SOUND_ALERT_COOLDOWN_SECONDS)
            {
                System.Diagnostics.Debug.WriteLine("Sound alert on cooldown");
                return; // Skip if we played a sound recently
            }

            try
            {
                string? soundFile = null;
                
                // First: Check for user's custom alert.wav in the exe folder
                string customSoundPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "alert.wav");
                System.Diagnostics.Debug.WriteLine($"Checking for custom sound: {customSoundPath}");
                
                if (File.Exists(customSoundPath))
                {
                    soundFile = customSoundPath;
                    System.Diagnostics.Debug.WriteLine("Found custom alert.wav");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("Custom alert.wav not found");
                    
                    // Second: Use the default sound included with the application
                    string defaultSoundPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sounds", "default_alert.wav");
                    System.Diagnostics.Debug.WriteLine($"Checking for default sound: {defaultSoundPath}");
                    
                    if (File.Exists(defaultSoundPath))
                    {
                        soundFile = defaultSoundPath;
                        System.Diagnostics.Debug.WriteLine("Found default alert sound");
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine("Default alert sound not found");
                    }
                }
                
                if (soundFile != null)
                {
                    System.Diagnostics.Debug.WriteLine($"Attempting to play: {soundFile}");
                    
                    // Play sound asynchronously on background thread
                    string fileToPlay = soundFile; // Capture for async operation
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            using (var audioFile = new AudioFileReader(fileToPlay))
                            using (var outputDevice = new WaveOutEvent())
                            {
                                outputDevice.Init(audioFile);
                                outputDevice.Play();
                                
                                System.Diagnostics.Debug.WriteLine("Sound playback started");
                                
                                // Wait for sound to finish
                                while (outputDevice.PlaybackState == PlaybackState.Playing)
                                {
                                    System.Threading.Thread.Sleep(10);
                                }
                                
                                System.Diagnostics.Debug.WriteLine("Sound playback finished");
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error in async playback: {ex.Message}");
                        }
                    });
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("No sound file found, playing system beep");
                    // Play system beep asynchronously
                    System.Threading.Tasks.Task.Run(() =>
                    {
                        try
                        {
                            Console.Beep(800, 200);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Beep failed: {ex.Message}");
                        }
                    });
                }
                
                lastSoundAlert = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error playing sound: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Fallback to system beep if there's an error
                System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        Console.Beep(800, 200);
                    }
                    catch (Exception beepEx)
                    {
                        System.Diagnostics.Debug.WriteLine($"Beep also failed: {beepEx.Message}");
                    }
                });
                
                lastSoundAlert = DateTime.Now;
            }
        }

        private void SaveData()
        {
            try
            {
                var data = new AppData
                {
                    NoiseThreshold = noiseThreshold,
                    RewardPoints = rewardPoints,
                    PenaltyPoints = penaltyPoints,
                    Sensitivity = sensitivity,
                    SilenceDuration = silenceDuration,
                    CelebrationDuration = celebrationDuration,
                    Points = points,
                    SoundAlertEnabled = soundAlertEnabled
                };

                string json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(DATA_FILE, json);
            }
            catch (Exception ex)
            {
                // Silently fail - don't interrupt the app
                System.Diagnostics.Debug.WriteLine($"Error saving data: {ex.Message}");
            }
        }

        private void LoadData()
        {
            try
            {
                if (File.Exists(DATA_FILE))
                {
                    string json = File.ReadAllText(DATA_FILE);
                    var data = JsonSerializer.Deserialize<AppData>(json);

                    if (data != null)
                    {
                        noiseThreshold = data.NoiseThreshold;
                        rewardPoints = data.RewardPoints;
                        penaltyPoints = data.PenaltyPoints;
                        sensitivity = data.Sensitivity;
                        silenceDuration = data.SilenceDuration;
                        celebrationDuration = data.CelebrationDuration;
                        points = data.Points;
                        soundAlertEnabled = data.SoundAlertEnabled;

                        // Update UI
                        PointsText.Text = $"{points}";
                    }
                }
            }
            catch (Exception ex)
            {
                // Silently fail - use defaults
                System.Diagnostics.Debug.WriteLine($"Error loading data: {ex.Message}");
            }
        }

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            var settings = new SettingsWindow(noiseThreshold, rewardPoints, penaltyPoints, sensitivity, silenceDuration, celebrationDuration, soundAlertEnabled);

            if (settings.ShowDialog() == true)
            {
                noiseThreshold = settings.Threshold;
                rewardPoints = settings.Reward;
                penaltyPoints = settings.Penalty;
                sensitivity = settings.Sensitivity;
                silenceDuration = settings.SilenceDuration;
                celebrationDuration = settings.CelebrationDuration;
                soundAlertEnabled = settings.SoundAlertEnabled;
                
                SaveData(); // Save settings immediately
            }
        }

        private void ResetPoints_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Are you sure you want to reset points to 0?",
                "Reset Points",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                points = 0;
                PointsText.Text = $"{points}";
                SaveData();
                GlowPointsContainer();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            SaveData(); // Save one final time before closing
            waveIn?.StopRecording();
            waveIn?.Dispose();
            base.OnClosed(e);
        }
    }
}