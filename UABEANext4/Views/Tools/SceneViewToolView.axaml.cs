using Avalonia.Controls;
using UABEANext4.ViewModels.Tools;
using System;

namespace UABEANext4.Views.Tools;

public partial class SceneViewToolView : UserControl
{
    public SceneViewToolView()
    {
        InitializeComponent();

        // Connect scene view control events to view model
        sceneViewControl.SelectionChanged += OnSceneSelectionChanged;

        // Connect view model actions when DataContext is set
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is SceneViewToolViewModel vm)
        {
            // Connect the reset camera action to the control
            vm.ResetCameraAction = () => sceneViewControl.ResetCamera();
        }
    }

    private void OnSceneSelectionChanged(object? sender, Logic.Scene.SceneObject? obj)
    {
        if (DataContext is SceneViewToolViewModel vm)
        {
            vm.OnObjectSelected(obj);
        }
    }
}
