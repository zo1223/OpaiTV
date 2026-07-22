using System.Windows;
using System.Windows.Controls;

namespace OrionTV.Desktop.App.Controls;

public partial class PageHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageHeader),
            new PropertyMetadata("", (d, e) => ((PageHeader)d).TitleText.Text = (string?)e.NewValue ?? ""));

    public static readonly DependencyProperty ShowBackProperty =
        DependencyProperty.Register(nameof(ShowBack), typeof(bool), typeof(PageHeader),
            new PropertyMetadata(true, (d, e) => ((PageHeader)d).BackButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public PageHeader()
    {
        InitializeComponent();
    }

    public event EventHandler? BackRequested;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public bool ShowBack
    {
        get => (bool)GetValue(ShowBackProperty);
        set => SetValue(ShowBackProperty, value);
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);
}
