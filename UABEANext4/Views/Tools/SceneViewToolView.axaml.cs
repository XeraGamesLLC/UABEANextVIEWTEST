using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using UABEANext4.ViewModels.Tools;
using System;
using System.ComponentModel;

namespace UABEANext4.Views.Tools;

public partial class SceneViewToolView : UserControl
{
    private SceneViewToolViewModel? _viewModel;

    public SceneViewToolView()
    {
        InitializeComponent();

        // Connect scene view control events to view model
        sceneViewControl.SelectionChanged += OnSceneSelectionChanged;
        sceneViewControl.DuplicateRequested += OnDuplicateRequested;
        sceneViewControl.DeleteRequested += OnDeleteRequested;

        // Connect view model actions when DataContext is set
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from previous view model
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (DataContext is SceneViewToolViewModel vm)
        {
            _viewModel = vm;

            // Connect the reset camera action to the control
            vm.ResetCameraAction = () => sceneViewControl.ResetCamera();

            // Subscribe to property changes to mark scene as dirty
            vm.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // When SceneData property changes (indicating scene modification), mark scene dirty
        if (e.PropertyName == nameof(SceneViewToolViewModel.SceneData))
        {
            sceneViewControl.MarkDirty();
        }
    }

    private void OnSceneSelectionChanged(object? sender, Logic.Scene.SceneObject? obj)
    {
        if (DataContext is SceneViewToolViewModel vm)
        {
            vm.OnObjectSelected(obj);
        }
    }

    private void OnDuplicateRequested(object? sender, EventArgs e)
    {
        if (DataContext is SceneViewToolViewModel vm)
        {
            vm.DuplicateObjectCommand.Execute(null);
        }
    }

    private void OnDeleteRequested(object? sender, EventArgs e)
    {
        if (DataContext is SceneViewToolViewModel vm)
        {
            vm.DeleteObjectCommand.Execute(null);
        }
    }

    private void OnSceneTabClick(object? sender, RoutedEventArgs e)
    {
        sceneTabButton.IsChecked = true;
        logTabButton.IsChecked = false;
        hierarchyTabButton.IsChecked = false;
        scenePanel.IsVisible = true;
        logPanel.IsVisible = false;
        hierarchyPanel.IsVisible = false;
        logControls.IsVisible = false;
    }

    private void OnLogTabClick(object? sender, RoutedEventArgs e)
    {
        sceneTabButton.IsChecked = false;
        logTabButton.IsChecked = true;
        hierarchyTabButton.IsChecked = false;
        scenePanel.IsVisible = false;
        logPanel.IsVisible = true;
        hierarchyPanel.IsVisible = false;
        logControls.IsVisible = true;
    }

    private void OnHierarchyTabClick(object? sender, RoutedEventArgs e)
    {
        sceneTabButton.IsChecked = false;
        logTabButton.IsChecked = false;
        hierarchyTabButton.IsChecked = true;
        scenePanel.IsVisible = false;
        logPanel.IsVisible = false;
        hierarchyPanel.IsVisible = true;
        logControls.IsVisible = false;
    }
}
