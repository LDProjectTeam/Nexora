using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Vellum.App.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private void NumericOnly_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = string.IsNullOrEmpty(e.Text) || !e.Text.All(char.IsDigit);
        }

        private void NumericOnly_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                var text = e.DataObject.GetData(typeof(string)) as string;
                if (string.IsNullOrEmpty(text) || !text.All(char.IsDigit))
                    e.CancelCommand();
            }
            else
            {
                e.CancelCommand();
            }
        }
    }
}
