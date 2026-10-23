using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SnipSnip
{
    public partial class Toast : Window
    {
        static Toast? current;
        readonly DispatcherTimer timer = new();
        Action? onClick;
        bool closing;

        Toast()
        {
            InitializeComponent();
            timer.Tick += (_, _) => FadeOut();
            MouseEnter += (_, _) => timer.Stop();
            MouseLeave += (_, _) => { if (timer.Interval > TimeSpan.Zero) timer.Start(); };
            SizeChanged += (_, _) => Place();
            Loaded += (_, _) =>
            {
                Place();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            };
        }

        /// <summary>Zeigt eine Benachrichtigung unten rechts. seconds = 0 -> bleibt offen.</summary>
        public static Toast Notify(string title, string message, BitmapSource? thumb, Action? onClick = null,
            int seconds = 5, params (string Label, Action Action)[] buttons)
        {
            current?.CloseNow();
            var t = new Toast();
            current = t;
            t.Thumb.Source = thumb;
            t.ThumbBox.Visibility = thumb == null ? Visibility.Collapsed : Visibility.Visible;
            t.Update(title, message, onClick, seconds, buttons);
            t.Opacity = 0;
            t.Show();
            return t;
        }

        public void Update(string title, string message, Action? click, int seconds,
            params (string Label, Action Action)[] buttons)
        {
            TitleText.Text = title;
            MessageText.Text = message;
            MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
            onClick = click;
            Card.Cursor = click != null ? Cursors.Hand : Cursors.Arrow;
            SetProgress(false);

            Buttons.Children.Clear();
            foreach (var (label, action) in buttons)
            {
                var b = new Button
                {
                    Content = label,
                    Style = (Style)FindResource("IconButton"),
                    Background = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                    Foreground = (Brush)FindResource("AccentTextBrush"),
                    Padding = new Thickness(10, 4, 10, 4),
                    Margin = new Thickness(2, 2, 4, 2),
                    FontSize = 12
                };
                b.Click += (_, _) => { action(); FadeOut(); };
                Buttons.Children.Add(b);
            }
            Buttons.Visibility = buttons.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

            timer.Stop();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            if (seconds > 0) timer.Start();
        }

        /// <summary>Liefert nur die Karte (ohne Fenster) – für die Doku-Bilder.</summary>
        internal static FrameworkElement CreateDocsCard(string title, string message, BitmapSource? thumb,
            params (string Label, Action Action)[] buttons)
        {
            var t = new Toast();
            t.Thumb.Source = thumb;
            t.ThumbBox.Visibility = thumb == null ? Visibility.Collapsed : Visibility.Visible;
            t.Update(title, message, null, 0, buttons);
            var card = (FrameworkElement)t.Content;
            t.Content = null;
            card.Width = t.Width;
            t.Close();
            return card;
        }

        public void SetProgress(bool on) => Progress.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        void Place()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth;
            Top = wa.Bottom - ActualHeight;
        }

        void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (onClick == null) return;
            var a = onClick;
            FadeOut();
            a();
        }

        void Close_Click(object sender, RoutedEventArgs e) => FadeOut();

        void FadeOut()
        {
            if (closing) return;
            closing = true;
            timer.Stop();
            var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(160));
            anim.Completed += (_, _) => CloseNow();
            BeginAnimation(OpacityProperty, anim);
        }

        void CloseNow()
        {
            closing = true;
            timer.Stop();
            if (current == this) current = null;
            try { Close(); } catch (InvalidOperationException) { }
        }
    }
}
