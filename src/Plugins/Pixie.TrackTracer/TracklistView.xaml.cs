using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace Pixie.TrackTracer;

public partial class TracklistView : UserControl
{
    public TracklistView()
    {
        InitializeComponent();
    }

    private void OnLinkRequested(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // a broken link in someone's description is not our problem to report
        }
        e.Handled = true;
    }
}
