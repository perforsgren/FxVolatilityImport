// MainWindow.xaml.cs
using System.Windows;
using Application = System.Windows.Application;

namespace FxVolatilityImport
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => ((App)Application.Current).ExitApplication();

        private void WidgetButton_Click(object sender, RoutedEventArgs e)
            => ((App)Application.Current).ShowWidget();
    }
}
