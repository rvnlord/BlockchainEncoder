using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommonLib.Source.Common.Converters;
using CommonLib.Source.Common.Extensions.Collections;
using CommonLib.Source.Common.Utils.TypeUtils;
using CommonLib.Source.Common.Utils.UtilClasses;
using CommonLib.Wpf.Source.Common.Utils;
using CommonLib.Wpf.Source.Common.Utils.TypeUtils;
using WpfMyCompression.Source.DbContext.Models;
using WpfMyCompression.Source.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;

namespace WpfMyCompression.Source.Windows
{
    public partial class MainWindow
    {
        private ILitecoinManager _lm;
        private CompressionEngine _ce;
        private string _sourceFIle;

        public ILitecoinManager Lm => _lm ??= new LitecoinManager();
        public CompressionEngine Ce => _ce ??= new CompressionEngine();

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }
        
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            WpfAsyncUtils.ShowLoader(gridMain);
            
            this.InitializeCommonComponents(Properties.Resources.NotifyIcon);
            Lm.RawBlockchainSyncStatusChanged += LitecoinManager_RawBlockchainSyncStatusChanged;
            await Lm.NotifyBlockchainSyncStatusChangedAsync();

            Ce.CompressionStatusChanged += CompressionEngine_CompressionStatusChanged;

            WpfAsyncUtils.HideLoader(gridMain);
        }

        private async Task LitecoinManager_RawBlockchainSyncStatusChanged(ILitecoinManager sender, LitecoinManager.RawBlockchainSyncStatusChangedEventArgs e, CancellationToken token)
        {
            pbStatus.Value = e.Block == null || e.Block.Index == 0 ? 0 : (double)e.Block.Index / e.LastBlock.Index * 100;
            lblOperation.Content = e.ToString();
            await Task.CompletedTask;
        }

        private async Task CompressionEngine_CompressionStatusChanged(CompressionEngine sender, CompressionEngine.CompressionStatusChangedEventArgs e, CancellationToken token)
        {
            pbStatus.Value = (double)e.FileOffset / e.FileSize * 100;
            lblOperation.Content = e.ToString();
            await Task.CompletedTask;
        }

        private async Task<ExceptionUtils.CaughtException<Exception>> CompressAsync()
        {
            return await ExceptionUtils.CatchAsync<Exception>(async () => await Ce.CompressAsync(_sourceFIle));
        }

        private async Task<ExceptionUtils.CaughtException<Exception>> DecompressAsync()
        {
            throw new NotImplementedException();
        }

        private async Task<ExceptionUtils.CaughtExceptionAndData<Exception, DbRawBlock>> SyncAsync()
        {
            return await ExceptionUtils.CatchAsync<Exception, DbRawBlock>(async () => await Lm.SyncRawBlockchain());
        }

        private async Task<ExceptionUtils.CaughtException<Exception>> PauseAsync()
        {
            return await ExceptionUtils.CatchAsync<Exception>(async () => await Lm.PauseSyncingRawBlockchainAsync());
        }

        private async void BtnCompressDecompress_Click(object sender, RoutedEventArgs e)
        {
            btnSyncPause.IsEnabled = false;
            btnCompressDecompress.IsEnabled = false;

            if (btnCompressDecompress.Content.ToString() == "Compress")
            {
                if (!File.Exists(_sourceFIle))
                    lblOperation.Content = "File doesn't exist";
                else
                {
                    lblOperation.Content = "Compressing...";
                    var compress = await CompressAsync();
                    if (compress.IsSuccess)
                        btnCompressDecompress.Content = "Decompress";
                    else
                        lblOperation.Content = compress.Error.Message;
                }
            }
            else if (btnCompressDecompress.Content.ToString() == "Decompress")
            {          
                var decompress = await DecompressAsync();
                if (decompress.IsSuccess)
                    btnCompressDecompress.Content = "Compress";
                else
                    lblOperation.Content = decompress.Error.Message;
            }
            
            btnCompressDecompress.IsEnabled = true;
            btnSyncPause.IsEnabled = true;
        }

        private async void BtnSyncPause_Click(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            btnCompressDecompress.IsEnabled = false;
            
            if (btnSyncPause.Content.ToString() == "Sync")
            {
                WpfAsyncUtils.ShowLoader(gridSourceFile);

                btnSyncPause.Content = "Pause";

                var sync = await SyncAsync();
                if (!sync.IsSuccess)
                {
                    lblOperation.Content = sync.Error.Message;
                    btnSyncPause.Content = "Sync";
                }

                WpfAsyncUtils.HideLoader(gridSourceFile);
            }
            else if (btnSyncPause.Content.ToString() == "Pause")
            {
                btnSyncPause.IsEnabled = false;
               
                var pause = await PauseAsync();
                if (!pause.IsSuccess)
                    lblOperation.Content = pause.Error.Message;
                else
                    btnSyncPause.Content = "Sync";

                btnSyncPause.IsEnabled = true;
            }
            
            btnCompressDecompress.IsEnabled = true;
        }
        
        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            txtSourceFile.Text = "Select or drop a file...";
            btnCompressDecompress.IsEnabled = false;
            btnCompressDecompress.Content = "Compress";
        }

        private void UpdateGuiForSelectedFile(string filePath)
        {
            var fi = new FileInfo(filePath);
            txtSourceFile.Text = $"{fi.Name} ({fi.Length.ToFileSizeString()})";
            _sourceFIle = fi.FullName;

            var extension = Path.GetExtension(filePath);
            btnCompressDecompress.IsEnabled = true;
            if (extension != ".lid")
                btnCompressDecompress.Content = "Compress";
            else
                btnCompressDecompress.Content = "Decompress";
        }
        
        private void BtnChooseSourceFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                //DefaultExt = ".txt",
                InitialDirectory = @"C:\"
            };
            
            var result = dlg.ShowDialog();

            if (!result.HasValue || !result.Value) 
                return;

            UpdateGuiForSelectedFile(dlg.FileName);
        }

        private void TxtSourceFile_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
                return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var filePath = files?[0] ?? throw new NullReferenceException();
            UpdateGuiForSelectedFile(filePath);
        }

        private void TxtSourceFile_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.All : DragDropEffects.None;
        }

        private void TxtSourceFile_PreviewDragOver(object sender, DragEventArgs e) => e.Handled = true;

        private async void MainWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            await PauseAsync();
        }
    }
}
