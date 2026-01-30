using System.Windows;
using System.IO;
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
            
            viewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsImporting))
            {
                var vm = (MainViewModel)DataContext;
                if (vm.IsImporting)
                {
                    StartSpinAnimation();
                }
                else
                {
                    StopSpinAnimation();
                }
            }
            else if (e.PropertyName == nameof(MainViewModel.ImportJustCompleted))
            {
                var vm = (MainViewModel)DataContext;
                if (vm.ImportJustCompleted)
                {
                    StartSuccessAnimation();
                }
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
            {
                StartSpinAnimation();
            }
        }

        private void WidgetWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (IsLoaded && WindowState == WindowState.Normal)
            {
                SavePosition();
            }
        }

        private async void ImportAllButton_Click(object sender, RoutedEventArgs e)
        {
            var vm = (MainViewModel)DataContext;
            
            // Ladda data först
            vm.LoadDataCommand.Execute(null);
            
            // Vänta tills laddning är klar
            while (vm.IsLoading)
            {
                await Task.Delay(100);
            }
            
            // Importera om data laddades
            if (vm.VolatilityData.Any())
            {
                vm.ImportAtmCommand.Execute(null);
                vm.ImportSmileCommand.Execute(null);
            }
        }

        private void SavePosition()
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir!);

                var content = $"{Left},{Top}";
                File.WriteAllText(SettingsPath, content);
            }
            catch
            {
            }
        }

        private void RestorePosition()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    var primaryScreen = Screen.PrimaryScreen!.WorkingArea;
                    Left = primaryScreen.Right - Width - 20;
                    Top = primaryScreen.Bottom - Height - 20;
                    return;
                }

                var content = File.ReadAllText(SettingsPath);
                var parts = content.Split(',');
                
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out double left) &&
                    double.TryParse(parts[1], out double top))
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
            }
            catch
            {
            }
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
            {
                Left = nearestScreen.WorkingArea.Right - Width - 20;
                Top = nearestScreen.WorkingArea.Bottom - Height - 20;
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WidgetClicked?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            WidgetClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}