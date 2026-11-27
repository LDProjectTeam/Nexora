using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Vellum.Core.Helpers;
using Vellum.Core.Models;

namespace Vellum.Bootstrapper
{
    public sealed class BootstrapperViewModel : INotifyPropertyChanged
    {
        private string _stageText = BootstrapperText.Get("CheckingUpdates");
        private string _message = BootstrapperText.Get("Subtitle");
        private double _progress;
        private bool _isIndeterminate = true;
        private string _details = string.Empty;
        private string _errorText = string.Empty;
        private bool _isFailed;
        private bool _isBusy = true;
        private bool _canRetry;
        private bool _canLaunchCurrent;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title => BootstrapperText.Get("Title");
        public string Subtitle => BootstrapperText.Get("Subtitle");

        public string StageText { get => _stageText; private set => Set(ref _stageText, value); }
        public string Message { get => _message; private set => Set(ref _message, value); }
        public double Progress { get => _progress; private set => Set(ref _progress, value); }
        public bool IsIndeterminate { get => _isIndeterminate; private set => Set(ref _isIndeterminate, value); }
        public string Details { get => _details; private set => Set(ref _details, value); }
        public string ErrorText { get => _errorText; private set => Set(ref _errorText, value); }
        public bool IsFailed
        {
            get => _isFailed;
            private set
            {
                if (!Set(ref _isFailed, value)) return;
                OnPropertyChanged(nameof(ShowRetry));
                OnPropertyChanged(nameof(ShowLaunchCurrent));
                OnPropertyChanged(nameof(IsBusy));
            }
        }

        public bool IsBusy
        {
            get => _isBusy && !IsFailed;
            private set
            {
                if (!Set(ref _isBusy, value)) return;
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(ShowRetry));
            }
        }

        public bool CanRetry { get => _canRetry; private set { if (Set(ref _canRetry, value)) OnPropertyChanged(nameof(ShowRetry)); } }
        public bool CanLaunchCurrent { get => _canLaunchCurrent; private set { if (Set(ref _canLaunchCurrent, value)) OnPropertyChanged(nameof(ShowLaunchCurrent)); } }

        public bool ShowRetry => IsFailed && CanRetry && !IsBusy;
        public bool ShowLaunchCurrent => IsFailed && CanLaunchCurrent && !IsBusy;
        public bool ShowError => IsFailed && !string.IsNullOrWhiteSpace(ErrorText);

        public void BeginRun()
        {
            StageText = BootstrapperText.Get("CheckingUpdates");
            Message = string.Empty;
            Progress = 0;
            IsIndeterminate = true;
            Details = string.Empty;
            ErrorText = string.Empty;
            CanRetry = false;
            CanLaunchCurrent = false;
            IsFailed = false;
            IsBusy = true;
            OnPropertyChanged(nameof(ShowError));
        }

        public void Apply(BootstrapperProgress progress)
        {
            var label = Label(progress.Stage);
            StageText = label;
            Message = string.Equals(progress.Message, label, StringComparison.Ordinal)
                ? string.Empty
                : progress.Message;
            Progress = Math.Clamp(progress.Fraction * 100, 0, 100);
            IsIndeterminate = progress.TotalBytes <= 0 && progress.Stage is not BootstrapperStage.StartingLauncher;

            var parts = new System.Collections.Generic.List<string>();
            if (progress.TotalBytes > 0)
            {
                parts.Add(string.Format(BootstrapperText.Get("DownloadedFormat"),
                    PathHelper.FormatBytes(progress.BytesDownloaded),
                    PathHelper.FormatBytes(progress.TotalBytes)));
            }
            if (progress.SpeedBytesPerSec > 0)
                parts.Add(string.Format(BootstrapperText.Get("SpeedFormat"),
                    PathHelper.FormatBytes(progress.SpeedBytesPerSec)));
            if (progress.SecondsRemaining > 0)
                parts.Add(string.Format(BootstrapperText.Get("RemainingFormat"),
                    PathHelper.FormatTimeRemaining(progress.SecondsRemaining)));
            Details = string.Join("  ·  ", parts);

            if (progress.Stage == BootstrapperStage.UpdateFailed)
            {
                IsFailed = true;
                IsBusy = false;
                ErrorText = progress.ErrorMessage ?? progress.Message;
                CanRetry = progress.CanRetry;
                CanLaunchCurrent = progress.CanLaunchCurrent;
                OnPropertyChanged(nameof(ShowError));
            }
            else
            {
                IsFailed = false;
                IsBusy = true;
                ErrorText = string.Empty;
                OnPropertyChanged(nameof(ShowError));
            }
        }

        public void SetFailure(string message, bool canRetry, bool canLaunchCurrent)
        {
            Apply(new BootstrapperProgress
            {
                Stage = BootstrapperStage.UpdateFailed,
                Message = BootstrapperText.Get("UpdateFailed"),
                ErrorMessage = message,
                CanRetry = canRetry,
                CanLaunchCurrent = canLaunchCurrent
            });
        }

        public void SetStarting()
        {
            Apply(new BootstrapperProgress
            {
                Stage = BootstrapperStage.StartingLauncher,
                Message = BootstrapperText.Get("StartingLauncher"),
                Fraction = 1
            });
        }

        private static string Label(BootstrapperStage stage) => stage switch
        {
            BootstrapperStage.CheckingUpdates => BootstrapperText.Get("CheckingUpdates"),
            BootstrapperStage.NoUpdates => BootstrapperText.Get("NoUpdates"),
            BootstrapperStage.UpdatingBootstrapper => BootstrapperText.Get("UpdatingBootstrapper"),
            BootstrapperStage.CheckingIntegrity => BootstrapperText.Get("CheckingIntegrity"),
            BootstrapperStage.AllChecksPassed => BootstrapperText.Get("AllChecksPassed"),
            BootstrapperStage.DownloadingUpdate => BootstrapperText.Get("DownloadingUpdate"),
            BootstrapperStage.InstallingUpdate => BootstrapperText.Get("InstallingUpdate"),
            BootstrapperStage.VerifyingFiles => BootstrapperText.Get("VerifyingFiles"),
            BootstrapperStage.StartingLauncher => BootstrapperText.Get("StartingLauncher"),
            BootstrapperStage.UpdateFailed => BootstrapperText.Get("UpdateFailed"),
            _ => BootstrapperText.Get("Subtitle")
        };

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
        {
            if (Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(property);
            return true;
        }

        private void OnPropertyChanged(string? property)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }
}
