// Views/WidgetWindow.xaml.cs
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using FxVolatilityImport.ViewModels;
using Screen = System.Windows.Forms.Screen;

namespace FxVolatilityImport.Views
{
    public partial class WidgetWindow : Window
    {
        public event EventHandler? WidgetClicked;

        private Storyboard? _spinAnimation;
        private Storyboard? _successAnimation;

        private static readonly string SettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FxVolatilityImport",
            "widget_position.txt");

        public WidgetWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;

            Loaded += WidgetWindow_Loaded;
            LocationChanged += WidgetWindow_LocationChanged;
            Closed += (_, _) => viewModel.PropertyChanged -= ViewModel_PropertyChanged;

            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsImporting))
            {
                var vm = (MainViewModel)DataContext;
                if (vm.IsImporting)
                    StartSpinAnimation();
                else
                    StopSpinAnimation();
            }
            else if (e.PropertyName == nameof(MainViewModel.ImportJustCompleted))
            {
                var vm = (MainViewModel)DataContext;
                if (vm.ImportJustCompleted)
                    StartSuccessAnimation();
            }
        }

        private void StartSpinAnimation()
        {
            _spinAnimation ??= (Storyboard)FindResource("SpinAnimation");
            _spinAnimation.Begin(this, true);
        }

        private void StopSpinAnimation()
        {
            _spinAnimation?.Stop(this);
        }

        private void StartSuccessAnimation()
        {
            _successAnimation ??= (Storyboard)FindResource("SuccessFadeOut");
            _successAnimation.Begin(this, true);
        }

        private void WidgetWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RestorePosition();

            var vm = (MainViewModel)DataContext;
            if (vm.IsImporting)
                StartSpinAnimation();
        }

        private void WidgetWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (IsLoaded && WindowState == WindowState.Normal)
                SavePosition();
        }

        // "Load & import all"-knappen binder direkt till MainViewModel.LoadAndImportCommand i XAML,
        // så ingen click-handler behövs här längre.

        private void SavePosition()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

                // InvariantCulture + ';' – med svenska inställningar blir annars "1234,5" och split på ',' går sönder
                var content = string.Create(CultureInfo.InvariantCulture, $"{Left};{Top}");
                File.WriteAllText(SettingsPath, content);
            }
            catch
            {
                // Position är bara bekvämlighet – ignorera fel
            }
        }

        private void RestorePosition()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    MoveToCorner(Screen.PrimaryScreen!);
                    return;
                }

                var parts = File.ReadAllText(SettingsPath).Split(';');

                if (parts.Length == 2 &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double left) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double top))
                {
                    var position = new System.Drawing.Point((int)left + 50, (int)top + 50);

                    if (IsPositionOnAnyScreen(position))
                    {
                        Left = left;
                        Top = top;
                    }
                    else
                    {
                        MoveToNearestScreen(left, top);
                    }
                }
                else
                {
                    // Gammalt format eller trasig fil
                    MoveToCorner(Screen.PrimaryScreen!);
                }
            }
            catch
            {
                // ignorera – fönstret hamnar där WPF placerar det
            }
        }

        private void MoveToCorner(Screen screen)
        {
            Left = screen.WorkingArea.Right - Width - 20;
            Top = screen.WorkingArea.Bottom - Height - 20;
        }

        private static bool IsPositionOnAnyScreen(System.Drawing.Point point)
        {
            foreach (var screen in Screen.AllScreens)
            {
                if (screen.WorkingArea.Contains(point))
                    return true;
            }
            return false;
        }

        private void MoveToNearestScreen(double savedLeft, double savedTop)
        {
            var savedPoint = new System.Drawing.Point((int)savedLeft, (int)savedTop);
            Screen? nearestScreen = null;
            double minDistance = double.MaxValue;

            foreach (var screen in Screen.AllScreens)
            {
                var centerX = screen.WorkingArea.Left + screen.WorkingArea.Width / 2;
                var centerY = screen.WorkingArea.Top + screen.WorkingArea.Height / 2;

                var distance = Math.Sqrt(
                    Math.Pow(savedPoint.X - centerX, 2) +
                    Math.Pow(savedPoint.Y - centerY, 2));

                if (distance < minDistance)
                {
                    minDistance = distance;
                    nearestScreen = screen;
                }
            }

            if (nearestScreen != null)
                MoveToCorner(nearestScreen);
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WidgetClicked?.Invoke(this, EventArgs.Empty);
            }
            else if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { /* musknappen släpptes innan DragMove startade */ }
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            WidgetClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}