using System;
using System.Net;
using CommonLib.Source.Common.Utils.UtilClasses;

namespace BlockchainEncoder.Source.Models.ViewModels
{
    public class CompressionConfigVM
    {
        private int _batches;
        private NetworkCredential _ltcRpcCredentials;

        public int Batches
        {
            get => _batches;
            set
            {
                _batches = value;
                OnPropertyChanging(nameof(Batches), true);
            }
        }

        public NetworkCredential LtcRpcCredentials
        {
            get => _ltcRpcCredentials;
            set
            {
                _ltcRpcCredentials = value;
                OnPropertyChanging(nameof(LtcRpcCredentials), true);
            }
        }

        public event MyEventHandler<CompressionConfigVM, CompressionConfigPropertyChangedEventArgs> PropertyChanged;
        private void OnPropertyChanging(CompressionConfigPropertyChangedEventArgs e) => PropertyChanged?.Invoke(this, e);
        private void OnPropertyChanging(string propertyName, bool setControlValue) => OnPropertyChanging(new CompressionConfigPropertyChangedEventArgs(propertyName, setControlValue));
        public void NotifyPropertyChanged(string propertyName, bool setControlValue) => OnPropertyChanging(propertyName, setControlValue);

        public class CompressionConfigPropertyChangedEventArgs : EventArgs
        {
            public string PropertyName { get; }
            public bool SetControlValue { get; }

            public CompressionConfigPropertyChangedEventArgs(string propertyName, bool setControlValue)
            {
                PropertyName = propertyName;
                SetControlValue = setControlValue;
            }
        }
    }
}
