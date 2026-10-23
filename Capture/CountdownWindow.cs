using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SnipSnip
{
    /// <summary>Großer Countdown für die verzögerte Aufnahme.</summary>
    internal sealed class CountdownWindow : Window
    {
        readonly TextBlock number;

        public CountdownWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            IsHitTestVisible = false;

            number = new TextBlock
            {
                FontSize = 64,
                FontWeight = FontWeights.Light,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -6, 0, 0)
            };
            Content = new Border
            {
                Width = 140,
                Height = 140,
                Margin = new Thickness(20),
                CornerRadius = new CornerRadius(70),
                Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x1D, 0x1D, 0x22)),
                BorderBrush = (Brush)Application.Current.FindResource("AccentBrush"),
                BorderThickness = new Thickness(3),
                Child = number,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 2, Opacity = 0.5 }
            };
        }

        public void Set(int n)
        {
            number.Text = n.ToString();
            number.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, System.TimeSpan.FromMilliseconds(250)));
        }
    }
}
