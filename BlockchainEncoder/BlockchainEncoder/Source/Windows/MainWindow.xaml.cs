using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BlockchainEncoder.Source.DbContext.Models;
using BlockchainEncoder.Source.Models.ViewModels;
using BlockchainEncoder.Source.Services;
using CommonLib.Source.Common.Converters;
using CommonLib.Source.Common.Extensions;
using CommonLib.Source.Common.Utils;
using CommonLib.Source.Common.Utils.TypeUtils;
using CommonLib.Source.Common.Utils.UtilClasses;
using CommonLib.Wpf.Source.Common.Utils;
using CommonLib.Wpf.Source.Common.Utils.TypeUtils;
using Microsoft.Extensions.Configuration;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Path = System.IO.Path;

namespace BlockchainEncoder.Source.Windows
{
    public partial class MainWindow
    {
        private ILitecoinManager _lm;
        private CompressionEngine _prevCe;
        private string _sourceFIle;
        private CompressionConfigVM _configVM;
        private readonly string _configFilePath = "appsettings.json"; // by default setting app setting uses 'Entry Assembly' (which would be correct for 'ASP.NET' but here, not only we require 'Executing Assembly' but 'Entry Assembly' would get the file beefore copying to the app dir, so essentially the one that is not updated)

        public ILitecoinManager Lm => _lm ??= new LitecoinManager(); // `_configVM.LtcRpcCredentials` doesn't need to be passed directly since `appsettings` should always be current
        public CompressionEngine Ce
        {
            get
            {
                var ce = new CompressionEngine(_configVM.Batches, 0, 0, false);
                if (ce.Equals(_prevCe))
                    return _prevCe;

                if (_prevCe != null)
                    _prevCe.CompressionStatusChanged -= CompressionEngine_CompressionStatusChanged;
                ce.CompressionStatusChanged += CompressionEngine_CompressionStatusChanged;
                _prevCe = ce;

                return _prevCe;
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }
        
        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();
            
            Logger.For<MainWindow>().Info("Starting Application");
            WpfAsyncUtils.ShowLoader(gridMain);
            
            this.InitializeCommonComponents(BlockchainEncoder.Properties.Resources.NotifyIcon);

            _configVM = new CompressionConfigVM
            {
                Batches = ConfigUtils.GetFromAppSettings().GetSection("Compression").GetValue<int>("Batches"),
                LtcRpcCredentials = ConfigUtils.GetRPCNetworkCredential("Litecoin")
            };
            _configVM.PropertyChanged += CompressionConfig_PropertyChanged;
            
            _configVM.NotifyPropertyChanged(nameof(_configVM.LtcRpcCredentials), true); // Binding manually to avoid value converters
            _configVM.NotifyPropertyChanged(nameof(_configVM.Batches), true);
            
            Lm.RawBlockchainSyncStatusChanged += LitecoinManager_RawBlockchainSyncStatusChanged;
            var initialSyncStatus = await ExceptionUtils.CatchAsync<Exception>(async () => await Lm.NotifyBlockchainSyncStatusChangedAsync());
            if (!initialSyncStatus.IsSuccess)
                lblOperation.Content = "Can't connect to the blockchain";
            
            WpfAsyncUtils.HideLoader(gridMain);
        }

        private async void CompressionConfig_PropertyChanged(CompressionConfigVM sender, CompressionConfigVM.CompressionConfigPropertyChangedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            if (e.PropertyName == nameof(CompressionConfigVM.LtcRpcCredentials))
            {
                var rpcAddress = _configVM.LtcRpcCredentials.Domain.BetweenOrWhole("://", "/");

                if (e.SetControlValue)
                {
                    xmeLTCNodeAddress.Value = rpcAddress;
                    xteLTCNodeUser.Value = _configVM.LtcRpcCredentials.UserName;
                    pwdLTCNodePassword.Password = _configVM.LtcRpcCredentials.Password; // PasswordBox is not bindable in WPF
                }
                
                await ConfigUtils.SetAppSettingValueAsync("RPCs:Litecoin:Address", rpcAddress, _configFilePath);
                await ConfigUtils.SetAppSettingValueAsync("RPCs:Litecoin:User", _configVM.LtcRpcCredentials.UserName, _configFilePath);
                await ConfigUtils.SetAppSettingValueAsync("RPCs:Litecoin:Password", _configVM.LtcRpcCredentials.Password, _configFilePath);
            }
            else if (e.PropertyName == nameof(CompressionConfigVM.Batches))
            {
                await ConfigUtils.SetAppSettingValueAsync("Compression:Batches", _configVM.Batches.ToString(), _configFilePath);

                if (e.SetControlValue)
                    xneBatchesOfBytesAllocatedPerBlock.Value = _configVM.Batches;
            }
        }

        private void XmeLTCNodeAddress_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            _configVM.LtcRpcCredentials.Domain = $"http://{xmeLTCNodeAddress.Value}/";
            _configVM.NotifyPropertyChanged(nameof(_configVM.LtcRpcCredentials), false); 
        }

        private void XteLTCNodeUser_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            _configVM.LtcRpcCredentials.UserName = xteLTCNodeUser.Value?.ToString() ?? string.Empty;
            _configVM.NotifyPropertyChanged(nameof(_configVM.LtcRpcCredentials), false); 
        }

        private void PwdLTCNodePassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            _configVM.LtcRpcCredentials.Password = pwdLTCNodePassword.Password;
            _configVM.NotifyPropertyChanged(nameof(_configVM.LtcRpcCredentials), false); 
        }

        private void XneBatchesOfBytesAllocatedPerBlock_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            _configVM.Batches = xneBatchesOfBytesAllocatedPerBlock.Value.ToInt();
            _configVM.NotifyPropertyChanged(nameof(_configVM.Batches), false); 
        }

        private async Task LitecoinManager_RawBlockchainSyncStatusChanged(ILitecoinManager sender, LitecoinManager.RawBlockchainSyncStatusChangedEventArgs e, CancellationToken token)
        {
            pbStatus.Value = e.Block == null || e.Block.Index == 0 ? 0 : (double)e.Block.Index / e.LastBlock.Index * 100;
            lblOperation.Content = e.ToString();
            await Task.CompletedTask;
        }

        private async Task CompressionEngine_CompressionStatusChanged(CompressionEngine sender, CompressionEngine.CompressionStatusChangedEventArgs e, CancellationToken token)
        {
            await Dispatcher.Invoke(async () =>
            {
                if (e.FileSize > 0)
                    pbStatus.Value = e.FileOffset == 0 ? 0 : (double)e.FileOffset / e.FileSize * 100;
                else if (e.Percentage is >= 0 and <= 100)
                    pbStatus.Value = (double) e.Percentage;

                lblOperation.Content = e.ToString();
                await Task.CompletedTask;
            });
        }

        private async Task<ExceptionUtils.CaughtExceptionAndData<Exception, string>> CompressAsync()
        {
            return await ExceptionUtils.CatchAsync<Exception, string>(async () => await Ce.CompressAsync(_sourceFIle));
        }

        private async Task<ExceptionUtils.CaughtExceptionAndData<Exception, string>> DecompressAsync()
        {
            return await ExceptionUtils.CatchAsync<Exception, string>(async () => await Ce.DecompressAsync(_sourceFIle));
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
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            xmeLTCNodeAddress.IsEnabled = false;
            xteLTCNodeUser.IsEnabled = false;
            pwdLTCNodePassword.IsEnabled = false;
            xneBatchesOfBytesAllocatedPerBlock.IsEnabled = false;

            btnSyncPause.IsEnabled = false;
            btnCompressDecompress.IsEnabled = false;
            btnClear.IsEnabled = false;
            btnChooseSourceFile.IsEnabled = false;
            txtSourceFile.IsEnabled = false;

            WpfAsyncUtils.ShowLoader(gridSourceFile);

            if (btnCompressDecompress.Content.ToString() == "Compress")
            {
                if (!File.Exists(_sourceFIle))
                    lblOperation.Content = "File doesn't exist";
                else
                {
                    lblOperation.Content = "Compressing...";
                    var compress = await CompressAsync();
                    if (compress.IsSuccess)
                        UpdateGuiForSelectedFile(compress.Data);
                    else
                        lblOperation.Content = compress.Error.Message;
                }
            }
            else if (btnCompressDecompress.Content.ToString() == "Decompress")
            {          
                if (!File.Exists(_sourceFIle) || !_sourceFIle.EndsWith(".lid"))
                    lblOperation.Content = "File doesn't exist or has wrong extension";
                else
                {
                    lblOperation.Content = "Deompressing...";
                    var decompress = await DecompressAsync();
                    if (decompress.IsSuccess)
                        UpdateGuiForSelectedFile(decompress.Data);
                    else
                        lblOperation.Content = decompress.Error.Message;
                }
            }

            WpfAsyncUtils.HideLoader(gridSourceFile);
            
            txtSourceFile.IsEnabled = true;
            btnChooseSourceFile.IsEnabled = true;
            btnClear.IsEnabled = true;
            btnCompressDecompress.IsEnabled = true;
            btnSyncPause.IsEnabled = true;

            xmeLTCNodeAddress.IsEnabled = true;
            xteLTCNodeUser.IsEnabled = true;
            pwdLTCNodePassword.IsEnabled = true;
            xneBatchesOfBytesAllocatedPerBlock.IsEnabled = true;
        }

        private async void BtnSyncPause_Click(object sender, RoutedEventArgs e)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(7))
                throw new PlatformNotSupportedException();

            xmeLTCNodeAddress.IsEnabled = false;
            xteLTCNodeUser.IsEnabled = false;
            pwdLTCNodePassword.IsEnabled = false;
            xneBatchesOfBytesAllocatedPerBlock.IsEnabled = false;

            btnCompressDecompress.IsEnabled = false;
            
            if (btnSyncPause.Content.ToString() == "Sync")
            {
                WpfAsyncUtils.ShowLoader(gridSourceFile);

                lblOperation.Content = "Syncing...";
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

            xmeLTCNodeAddress.IsEnabled = true;
            xteLTCNodeUser.IsEnabled = true;
            pwdLTCNodePassword.IsEnabled = true;
            xneBatchesOfBytesAllocatedPerBlock.IsEnabled = true;
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
            Logger.For<MainWindow>().Info("Closing Application");
        }
    }
}
