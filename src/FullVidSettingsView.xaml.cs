using System.Windows.Controls;

namespace FullVid
{
    public partial class FullVidSettingsView : UserControl
    {
        public FullVidSettingsView()
        {
            InitializeComponent();
            // DataContext is set by Playnite to the ISettings object from GetSettings().
        }
    }
}
