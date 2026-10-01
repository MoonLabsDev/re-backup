using System.Windows;
using ReBackup.App.Services;

namespace ReBackup.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }
}
