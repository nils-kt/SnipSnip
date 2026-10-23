using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SnipSnip
{
    public partial class HistoryWindow : Window
    {
        List<UploadRecord> items = new();

        public HistoryWindow()
        {
            InitializeComponent();
            Reload();
        }

        public void Reload()
        {
            items = UploadHistory.Load();
            UploadList.ItemsSource = null;
            UploadList.ItemsSource = items;
            CountText.Text = items.Count == 0 ? "Noch keine Uploads." : $"{items.Count} Upload(s)";
            UpdateButtons();
        }

        UploadRecord? Current => UploadList.SelectedItem as UploadRecord;

        void UpdateButtons() => DeleteBtn.IsEnabled = Current?.CanDelete == true;

        void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

        void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (Current != null) App.OpenUrl(Current.Url);
        }

        void Open_Click(object sender, RoutedEventArgs e)
        {
            if (Current != null) App.OpenUrl(Current.Url);
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (Current == null) return;
            ImageUtil.CopyText(Current.Url);
            CountText.Text = "Link kopiert ✓";
        }

        void DeleteOnline_Click(object sender, RoutedEventArgs e)
        {
            if (Current?.DeleteUrl != null) App.OpenUrl(Current.DeleteUrl);
        }

        void Remove_Click(object sender, RoutedEventArgs e)
        {
            if (Current == null) return;
            items.Remove(Current);
            UploadHistory.Save(items);
            Reload();
        }
    }
}
