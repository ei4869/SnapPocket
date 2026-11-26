using System.Windows;
using System.Windows.Input;

namespace SnapPocket.UI.Capture;

public partial class QuickRenamePopup : Window
{
    public string EnteredFileName { get; private set; } = string.Empty;

    public QuickRenamePopup(string defaultBaseName, string extension, string targetDirectory)
    {
        InitializeComponent();

        FileNameBox.Text = defaultBaseName;
        ExtText.Text = extension;
        TargetDirText.Text = targetDirectory;
        TargetDirText.ToolTip = targetDirectory;

        Loaded += (_, _) =>
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Left + (workArea.Width - ActualWidth) / 2.0;
            Top = workArea.Top + 26;

            Activate();
            FileNameBox.Focus();
            FileNameBox.SelectAll();
        };
    }

    private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Confirm();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm()
    {
        EnteredFileName = FileNameBox.Text.Trim();
        DialogResult = true;
        Close();
    }
}
