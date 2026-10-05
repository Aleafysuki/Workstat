using System.Windows;

namespace WorkTimer.App.Views;

/// <summary>极简输入框，避免为了"重命名项目"引一个完整对话框。</summary>
public partial class InputDialog : Window
{
    private InputDialog(string title, string prompt, string initial)
    {
        InitializeComponent();

        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;

        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    public string Value => ValueBox.Text;

    public static string? Show(Window owner, string title, string prompt, string initial)
    {
        var dialog = new InputDialog(title, prompt, initial) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
