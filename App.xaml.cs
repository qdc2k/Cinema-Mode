using System.Windows;

namespace CinemaMode
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            this.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"An unexpected error occurred: {args.Exception.Message}", "Cinema Mode Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }
    }
}