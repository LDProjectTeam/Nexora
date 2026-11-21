using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Nexora.App.Services;

namespace Nexora.App.ViewModels
{
    public sealed class AccentOption : ViewModelBase
    {
        private static readonly IReadOnlyList<AccentOption> _all = new List<AccentOption>
        {
            new AccentOption("White", "#D5D5D5", "#FFFFFF"),
            new AccentOption("Emerald", "#10B981", "#34D399"),
            new AccentOption("Blue", "#3B82F6", "#60A5FA"),
            new AccentOption("Amber", "#F59E0B", "#FBBF24"),
            new AccentOption("Violet", "#8B5CF6", "#A78BFA"),
            new AccentOption("Rose", "#F43F5E", "#FB7185"),
        };

        static AccentOption()
        {
            LocalizationManager.LanguageChanged += (_, _) =>
            {
                foreach (var option in _all)
                    option.NotifyNameChanged();
            };
        }

        private bool _isSelected;

        private AccentOption(string code, string primaryHex, string hoverHex)
        {
            Code = code;
            PrimaryHex = primaryHex;
            HoverHex = hoverHex;

            var primaryBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(primaryHex));
            primaryBrush.Freeze();
            Brush = primaryBrush;

            var hoverBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hoverHex));
            hoverBrush.Freeze();
            HoverBrush = hoverBrush;
        }

        public string Code { get; }
        public string PrimaryHex { get; }
        public string HoverHex { get; }
        public SolidColorBrush Brush { get; }
        public SolidColorBrush HoverBrush { get; }
        public string Name => LocalizationManager.GetString("Accent" + Code);

        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public void NotifyNameChanged() => OnPropertyChanged(nameof(Name));

        public static IReadOnlyList<AccentOption> All => _all;

        public static AccentOption Find(string? code)
            => _all.FirstOrDefault(o => o.Code == code) ?? _all[0];

        /// <summary>Применяет акцент ко всему приложению через Application.Resources.</summary>
        public static void Apply(string? code)
        {
            var option = Find(code);
            foreach (var item in _all)
                item.IsSelected = item.Code == option.Code;

            ReplaceBrush("AccentBrush", option.PrimaryHex);
            ReplaceBrush("AccentHoverBrush", option.HoverHex);
        }

        private static void ReplaceBrush(string key, string hex)
        {
            if (Application.Current?.Resources == null)
                return;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Application.Current.Resources[key] = brush;
        }
    }
}
