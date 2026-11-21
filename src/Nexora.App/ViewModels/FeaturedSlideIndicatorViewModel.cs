using Nexora.App.Services;

namespace Nexora.App.ViewModels
{
    public sealed class FeaturedSlideIndicatorViewModel : ViewModelBase
    {
        private bool _isActive;

        public FeaturedSlideIndicatorViewModel(int index, bool isActive)
        {
            Index = index;
            _isActive = isActive;
        }

        public int Index { get; }
        public string AutomationName => LocalizationManager.GetString("SlideIndicatorFormat", Index + 1);

        public bool IsActive
        {
            get => _isActive;
            set => SetProperty(ref _isActive, value);
        }
    }
}
