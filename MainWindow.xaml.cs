// MainWindow.xaml.cs
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;

namespace FxVolatilityImport
{
    public partial class MainWindow : Window
    {
        private static readonly Geometry MaximizeIcon = Geometry.Parse("M0.5,0.5 H9.5 V9.5 H0.5 Z");
        private static readonly Geometry RestoreIcon = Geometry.Parse("M2.5,0.5 H9.5 V7.5 M0.5,2.5 H7.5 V9.5 H0.5 Z");

        public MainWindow()
        {
            InitializeComponent();
            StateChanged += (_, _) => UpdateWindowState();
        }

        private void UpdateWindowState()
        {
            // Med WindowChrome hamnar fönsterkanten utanför skärmen när fönstret är maximerat – kompensera
            var maximized = WindowState == WindowState.Maximized;
            RootBorder.Margin = maximized ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            RootBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
            MaxIcon.Data = maximized ? RestoreIcon : MaximizeIcon;
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => ((App)Application.Current).ExitApplication();

        private void WidgetButton_Click(object sender, RoutedEventArgs e)
            => ((App)Application.Current).ShowWidget();
    }
}