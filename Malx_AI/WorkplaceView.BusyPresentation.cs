using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Malx_AI;

public partial class WorkplaceView
{
    private bool _workplaceProcessing;
    // All existing run paths share this setter, including cancellation and error cleanup.
    private bool _isProcessing
    {
        get => _workplaceProcessing;
        set
        {
            bool changed = _workplaceProcessing != value;
            _workplaceProcessing = value;
            void Update()
            {
                if (changed)
                {
                    if (value) BeginRunChangeSummary();
                    else CompleteRunChangeSummary();
                }
                UpdateWorkplaceBusyPresentation();
            }
            if (Dispatcher.CheckAccess()) Update();
            else Dispatcher.BeginInvoke(Update);
        }
    }

    private void UpdateWorkplaceBusyPresentation()
    {
        if (WorkplaceSendSpinner == null || WorkplaceSendArrow == null) return;
        bool wasSpinning = WorkplaceSendSpinner.Visibility == Visibility.Visible;
        WorkplaceSendArrow.Visibility = _isProcessing ? Visibility.Collapsed : Visibility.Visible;
        WorkplaceSendSpinner.Visibility = _isProcessing ? Visibility.Visible : Visibility.Collapsed;
        if (_isProcessing && !wasSpinning)
            WorkplaceSendRotation.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (!_isProcessing)
        {
            WorkplaceSendRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            WorkplaceSendRotation.Angle = 0;
        }
        System.Windows.Automation.AutomationProperties.SetHelpText(SendButton,
            _isProcessing ? "Working. Stop remains available; Agent Access can receive steering messages." : "Send message.");
    }
}
