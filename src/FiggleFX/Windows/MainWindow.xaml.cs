using HydraX.Library;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Reflection;
using System.Threading;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace HydraX
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// Active Instance
        /// </summary>
        private HydraInstance Instance = new HydraInstance();

        /// <summary>
        /// Gets or Sets the Log
        /// </summary>
        private StreamWriter LogStream { get; set; }

        /// <summary>
        /// Gets the ViewModel
        /// </summary>
        public MainViewModel ViewModel { get; } = new MainViewModel();

        /// <summary>
        /// Main Window Constructor
        /// </summary>
        public MainWindow()
        {
            InitializeComponent();
            DataContext = ViewModel;
            ViewModel.DimmerVisibility = Visibility.Hidden;
            Instance.Settings.Load("Settings.json");
            AssetListCheckBox.IsChecked = Instance.Settings["ExportAssetList", "Yes"] == "Yes";
            Bo4FxCheckBox.IsChecked = Instance.Settings["ExportBO4FX", "No"] == "Yes";
            Log("FiggleFX - Log Session Begin", "BEGIN");

            try
            {
                LogStream = new StreamWriter("Log.txt", true);
            }
            catch
            {
                LogStream = null;
            }

            // Started on Loaded rather than here: the update prompt is owned by
            // this window, which needs a handle before anything can be modal to it
            Loaded += (sender, e) =>
            {
                if (Instance.Settings["AutoUpdates", "Yes"] == "Yes")
                    new Thread(CheckForUpdate) { IsBackground = true }.Start();
            };
        }

        /// <summary>
        /// Offers the latest release, and installs it if the user says yes
        /// </summary>
        /// <remarks>
        /// Runs on a background thread at startup: a slow or unreachable GitHub
        /// must never hold up the window.
        /// </remarks>
        private void CheckForUpdate()
        {
            var release = FiggleUpdater.CheckForUpdate(Assembly.GetExecutingAssembly().GetName().Version);

            if (release == null)
                return;

            Log(string.Format("Update available: {0}", release.Tag), "INFO");

            var answer = Dispatcher.Invoke(() => MessageBox.Show(this,
                string.Format("FiggleFX {0} is available. Update and restart now?", release.Tag),
                "FiggleFX | Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information));

            if (answer != MessageBoxResult.Yes)
                return;

            // No zip attached to the release, so there is nothing to install
            if (string.IsNullOrEmpty(release.ZipUrl))
            {
                System.Diagnostics.Process.Start(FiggleUpdater.ReleasesPage);
                return;
            }

            // Title is a dependency property: it must be read on the UI thread,
            // and this method runs on a background one
            var title = (string)Dispatcher.Invoke((Func<string>)(() => Title));

            try
            {
                var payload = FiggleUpdater.Fetch(release, percent =>
                    Dispatcher.Invoke(() => Title = string.Format("FiggleFX | Downloading update... {0}%", percent)));

                Dispatcher.Invoke(() => Title = "FiggleFX | Installing update...");
                FiggleUpdater.ApplyAndRestart(payload);
                Log(string.Format("Installing {0}, restarting", release.Tag), "INFO");
                Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
            catch (Exception exception)
            {
                Log(string.Format("Update to {0} failed:\n\n{1}", release.Tag, exception), "ERROR");

                Dispatcher.Invoke(() =>
                {
                    Title = title;
                    MessageBox.Show(this,
                        "The update could not be installed, so nothing was changed. Opening the download page instead.",
                        "FiggleFX | Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    System.Diagnostics.Process.Start(FiggleUpdater.ReleasesPage);
                });
            }
        }

        /// <summary>
        /// Exports the asset on double click
        /// </summary>
        private void AssetListMouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (((FrameworkElement)e.OriginalSource).DataContext is Asset asset)
            {
                ViewModel.DimmerVisibility = Visibility.Visible;
                new ProgressWindow(ExportAssets, null, ProgressComplete, "Exporting Assets...", new List<Asset>() { asset }, 1, this).ShowDialog();
                ViewModel.DimmerVisibility = Visibility.Hidden;
            }
        }

        /// <summary>
        /// Persists an export option checkbox to Settings.json
        /// </summary>
        /// <remarks>
        /// Export reloads the settings file before it runs, so a toggle has to
        /// hit disk immediately rather than waiting for the window to close.
        /// </remarks>
        private void ExportOptionChanged(object sender, RoutedEventArgs e)
        {
            if (!(sender is CheckBox box) || box.Tag == null)
                return;

            Instance.Settings[box.Tag.ToString()] = box.IsChecked == true ? "Yes" : "No";
            Instance.Settings.Save("Settings.json");
        }

        /// <summary>
        /// Column header the list is currently sorted by, if any
        /// </summary>
        private GridViewColumnHeader SortedHeader = null;

        /// <summary>
        /// Direction the list is currently sorted in
        /// </summary>
        private ListSortDirection SortDirection = ListSortDirection.Ascending;

        /// <summary>
        /// Sorts the asset list by the clicked column, toggling direction on re-click
        /// </summary>
        private void AssetListHeaderClick(object sender, RoutedEventArgs e)
        {
            // The padding header at the end of the row carries no Tag
            if (!(e.OriginalSource is GridViewColumnHeader header) || header.Tag == null)
                return;

            if (header == SortedHeader)
            {
                SortDirection = SortDirection == ListSortDirection.Ascending ?
                    ListSortDirection.Descending :
                    ListSortDirection.Ascending;
            }
            else
            {
                if (SortedHeader != null)
                    SortedHeader.Content = HeaderText(SortedHeader);

                SortDirection = ListSortDirection.Ascending;
                SortedHeader = header;
            }

            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                ViewModel.SortBy(header.Tag.ToString(), SortDirection);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }

            // ▲ / ▼ - up/down arrow marking the sorted column
            header.Content = HeaderText(header) + (SortDirection == ListSortDirection.Ascending ? "  ▲" : "  ▼");
        }

        /// <summary>
        /// Gets a header's caption without any sort arrow
        /// </summary>
        private static string HeaderText(GridViewColumnHeader header)
        {
            return (header.Content as string ?? "").TrimEnd(' ', '▲', '▼');
        }

        /// <summary>
        /// Handles cleaning up on close
        /// </summary>
        private void MainWindowClosing(object sender, CancelEventArgs e)
        {
            LogStream?.Flush();
            LogStream?.Dispose();
            Instance.Settings.Save("Settings.json");
        }

        public void ExportAssets(object sender, DoWorkEventArgs e)
        {
            Instance.Settings.Load("Settings.json");

            var window = e.Argument as ProgressWindow;

            Parallel.ForEach(window.Data as List<Asset>, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, (asset, loop) =>
            {
                window.Worker.ReportProgress(0);

                try
                {
                    asset.Save(System.IO.Path.Combine(Instance.ExportFolder, asset.Type), Instance);
                    Log(string.Format("Exported {0}", asset.Name), "INFO");
                    asset.Status = "Exported";
                }
                catch (Exception exception)
                {
                    // Anything else we should log it
                    Log(string.Format("An unhandled exception while exporting {0}:\n\n{1}", asset.Name, exception), "ERROR");
                    asset.Status = "Error";
                }

                if (window.Worker.CancellationPending)
                    loop.Break();
            });
        }

        /// <summary>
        /// Writes to the log in debug
        /// </summary>
        private void Log(string value, string messageType)
        {
            if (LogStream != null)
            {
                lock (LogStream)
                {
                    LogStream.WriteLine("{0} [ {1} ] {2}", DateTime.Now.ToString("dd-MM-yyyy - HH:mm:ss"), messageType.PadRight(12), value);
                    LogStream.Flush();
                }
            }
        }

        /// <summary>
        /// Opens file dialog to open a package
        /// </summary>
        private void OpenGameClick(object sender, RoutedEventArgs e)
        {
            Title = "FiggleFX | Loading Game......";
            ViewModel.AssetButtonsEnabled = false;

            Instance.Clear();
            ViewModel.Assets.ClearAllItems();

            // Init Worker
            var worker = new BackgroundWorker();

            worker.DoWork += LoadGameWorker;
            worker.RunWorkerCompleted += ProgressComplete;

            worker.RunWorkerAsync();
        }

        private void LoadGameWorker(object sender, DoWorkEventArgs e)
        {
            Instance.LoadGame();
            ViewModel.Assets.AddRange(Instance.Assets);
        }

        /// <summary>
        /// Handles on progress complete
        /// </summary>
        private void ProgressComplete(object sender, RunWorkerCompletedEventArgs e)
        {
            GC.Collect();
            // ViewModel.DimmerVisibility = Visibility.Hidden;
            ViewModel.AssetButtonsEnabled = true;

            if (e.Error == null)
            {
                ViewModel.Assets.SendNotify();
                Title = string.Format("FiggleFX | Loaded {0} assets from {1}", Instance.Assets.Count, Instance.Game.Name);
            }
            else if(e.Error is GameNotFoundException)
            {
                Log("FiggleFX failed to find a supported game, please ensure one of them is running.", "ERROR");
                MessageBox.Show("FiggleFX failed to find a supported game, please ensure one of them is running.", "FiggleFX | Oops", MessageBoxButton.OK, MessageBoxImage.Error);
                Title = "FiggleFX";
                Instance.Clear();
            }
            else if (e.Error is GameNotSupportedException gnsException)
            {
                Log(string.Format("FiggleFX supports {0}, but not this version of it, if the game was recently updated, please wait for an update to FiggleFX.", gnsException.GameName), "ERROR");
                MessageBox.Show(string.Format("FiggleFX supports {0}, but not this version of it, if the game was recently updated, please wait for an update to FiggleFX.", gnsException.GameName), "FiggleFX | Oops", MessageBoxButton.OK, MessageBoxImage.Error);
                Title = "FiggleFX";
                Instance.Clear();
            }
            else if(e.Error is Exception exception)
            {
                Log(string.Format("An unhandled exception has occurred, take this to my creator:\n\n{0}", exception), "ERROR");
                MessageBox.Show(string.Format("An unhandled exception has occurred, take this to my creator:\n\n{0}", exception), "FiggleFX | Oops", MessageBoxButton.OK, MessageBoxImage.Error);
                Title = "FiggleFX";
                Instance.Clear();
            }
        }

        /// <summary>
        /// Exports all loaded assets
        /// </summary>
        private void ExportAllClick(object sender, RoutedEventArgs e)
        {
            var assets = AssetList.Items.Cast<Asset>().ToList();

            if (assets.Count == 0)
            {
                ViewModel.DimmerVisibility = Visibility.Visible;
                MessageBox.Show("There are no assets listed to export.", "FiggleFX | Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ViewModel.DimmerVisibility = Visibility.Hidden;
            }
            else
            {
                ViewModel.DimmerVisibility = Visibility.Visible;
                new ProgressWindow(ExportAssets, null, ProgressComplete, "Exporting Assets...", assets, assets.Count, this).ShowDialog();
                ViewModel.DimmerVisibility = Visibility.Hidden;
            }
        }

        /// <summary>
        /// Exports selected assets
        /// </summary>
        private void ExportSelectedClick(object sender, RoutedEventArgs e)
        {
            var assets = AssetList.SelectedItems.Cast<Asset>().ToList();

            if (assets.Count == 0)
            {
                ViewModel.DimmerVisibility = Visibility.Visible;
                MessageBox.Show("There are no assets listed to export.", "FiggleFX | Error", MessageBoxButton.OK, MessageBoxImage.Error);
                ViewModel.DimmerVisibility = Visibility.Hidden;
            }
            else
            {
                ViewModel.DimmerVisibility = Visibility.Visible;
                new ProgressWindow(ExportAssets, null, ProgressComplete, "Exporting Assets...", assets, assets.Count, this).ShowDialog();
                ViewModel.DimmerVisibility = Visibility.Hidden;
            }
        }
    }
}
