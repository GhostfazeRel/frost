using System.Windows;
using System.Windows.Controls.Primitives;

namespace Frost.App;

public partial class MainWindow
{
    // ---- preview pane width: drag the left edge, saved in settings ----
    private void PreviewGrip_DragDelta(object sender, DragDeltaEventArgs e) => _vm.SetPreviewWidth(_vm.PreviewWidth - e.HorizontalChange);

    // re-decode the picture for the new width once the drag is over
    private void PreviewGrip_DragCompleted(object sender, DragCompletedEventArgs e) => _vm.UpdatePreview();
}
