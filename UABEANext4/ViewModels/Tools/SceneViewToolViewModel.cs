using AssetsTools.NET.Extra;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Dock.Model.Mvvm.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using UABEANext4.AssetWorkspace;
using UABEANext4.Logic;
using UABEANext4.Logic.Scene;

namespace UABEANext4.ViewModels.Tools;

public partial class SceneViewToolViewModel : Tool
{
    const string TOOL_TITLE = "Scene View";
    const string DEFAULT_SCENE_NAME = "level0";

    public Workspace Workspace { get; }

    [ObservableProperty]
    private SceneData? _sceneData;

    [ObservableProperty]
    private SceneObject? _selectedObject;

    [ObservableProperty]
    private string _statusText = "No scene loaded. Load a scene or open a file.";

    [ObservableProperty]
    private bool _isSceneLoaded = false;

    [ObservableProperty]
    private string _sceneName = DEFAULT_SCENE_NAME;

    [ObservableProperty]
    private bool _autoLoad = true;

    /// <summary>
    /// Log entries for scene operations and diagnostics
    /// </summary>
    public ObservableCollection<SceneLogEntry> LogEntries { get; } = new();

    private List<AssetsFileInstance>? _currentFileInsts;
    private bool _hasAutoLoaded = false;
    private bool _isAutoLoading = false;

    /// <summary>
    /// Action to reset the camera. Set by the view to connect to the SceneViewControl.
    /// </summary>
    public Action? ResetCameraAction { get; set; }

    [Obsolete("This constructor is for the designer only and should not be used directly.", true)]
    public SceneViewToolViewModel()
    {
        Workspace = new();
        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;
    }

    public SceneViewToolViewModel(Workspace workspace)
    {
        Workspace = workspace;
        Id = TOOL_TITLE.Replace(" ", "");
        Title = TOOL_TITLE;

        WeakReferenceMessenger.Default.Register<SelectedWorkspaceItemChangedMessage>(this, OnWorkspaceItemSelected);
        WeakReferenceMessenger.Default.Register<WorkspaceClosingMessage>(this, OnWorkspaceClosing);

        // Subscribe to workspace items being added
        Workspace.RootItems.CollectionChanged += OnRootItemsChanged;
    }

    private void OnRootItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            Log(SceneLogLevel.Info, "Workspace items changed - new items added");

            // When new items are added, try auto-load if enabled
            if (AutoLoad && !_hasAutoLoaded && !IsSceneLoaded && !_isAutoLoading)
            {
                Log(SceneLogLevel.Info, $"Starting auto-load for scene '{SceneName}'");
                StartAutoLoadWithRetry();
            }
        }
    }

    private void StartAutoLoadWithRetry()
    {
        if (_isAutoLoading) return;
        _isAutoLoading = true;

        System.Threading.Tasks.Task.Run(async () =>
        {
            // Try multiple times with increasing delays to handle bundle files that need time to extract
            int[] delays = [100, 500, 1000, 2000, 3000];

            for (int i = 0; i < delays.Length; i++)
            {
                await System.Threading.Tasks.Task.Delay(delays[i]);

                // Check and try on UI thread, waiting for result
                bool success = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (!AutoLoad || _hasAutoLoaded || IsSceneLoaded)
                    {
                        return true; // Already loaded or disabled
                    }

                    Log(SceneLogLevel.Debug, $"Auto-load attempt {i + 1}/{delays.Length}");
                    return TryAutoLoadScene();
                });

                if (success)
                {
                    break;
                }
            }

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _isAutoLoading = false;
                if (!IsSceneLoaded && !_hasAutoLoaded)
                {
                    Log(SceneLogLevel.Warning, $"Auto-load failed: scene '{SceneName}' not found after all retries");
                }
            });
        });
    }

    /// <summary>
    /// Adds a log entry for scene operations
    /// </summary>
    public void Log(SceneLogLevel level, string message)
    {
        var entry = new SceneLogEntry(level, message);

        // Ensure we're on UI thread
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            LogEntries.Add(entry);
            // Keep log size reasonable (max 500 entries)
            while (LogEntries.Count > 500)
            {
                LogEntries.RemoveAt(0);
            }
        }
        else
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LogEntries.Add(entry);
                while (LogEntries.Count > 500)
                {
                    LogEntries.RemoveAt(0);
                }
            });
        }
    }

    [RelayCommand]
    private void ClearLog()
    {
        LogEntries.Clear();
        Log(SceneLogLevel.Info, "Log cleared");
    }

    private void OnWorkspaceItemSelected(object recipient, SelectedWorkspaceItemChangedMessage message)
    {
        var items = message.Value;
        if (items.Count == 0) return;

        var fileInsts = new List<AssetsFileInstance>();
        foreach (var item in items)
        {
            if (item.ObjectType == WorkspaceItemType.AssetsFile && item.Object is AssetsFileInstance fileInst)
            {
                fileInsts.Add(fileInst);
                Log(SceneLogLevel.Debug, $"Selected assets file: {item.Name}");
            }
            else if (item.ObjectType == WorkspaceItemType.BundleFile)
            {
                Log(SceneLogLevel.Debug, $"Selected bundle file: {item.Name}");
                // If a bundle file is selected, get all its child assets files
                foreach (var child in item.Children)
                {
                    if (child.ObjectType == WorkspaceItemType.AssetsFile && child.Object is AssetsFileInstance childFileInst)
                    {
                        fileInsts.Add(childFileInst);
                        Log(SceneLogLevel.Debug, $"  Found child assets file: {child.Name}");
                    }
                }
            }
        }

        if (fileInsts.Count > 0)
        {
            _currentFileInsts = fileInsts;
            StatusText = $"Ready to load scene from {_currentFileInsts.Count} file(s). Click 'Load Scene' to begin.";
            Log(SceneLogLevel.Info, $"Ready to load from {_currentFileInsts.Count} file(s)");

            // Auto-load if enabled and not already loaded
            if (AutoLoad && !_hasAutoLoaded && !IsSceneLoaded && !_isAutoLoading)
            {
                TryAutoLoadScene();
            }
        }
    }

    /// <summary>
    /// Attempts to auto-load the scene. Returns true if successful or already loaded.
    /// </summary>
    private bool TryAutoLoadScene()
    {
        // Try to find the scene by name in the workspace
        var sceneFile = FindSceneFileByName(SceneName);
        if (sceneFile != null)
        {
            Log(SceneLogLevel.Info, $"Found scene file for '{SceneName}'");
            StatusText = $"Auto-loading scene '{SceneName}'...";
            _currentFileInsts = new List<AssetsFileInstance> { sceneFile };
            _hasAutoLoaded = true;
            LoadScene();
            return true;
        }
        else
        {
            // Update status to show we're still waiting for the scene file
            StatusText = $"Waiting for scene '{SceneName}'... (Auto-load enabled)";
            Log(SceneLogLevel.Debug, $"Scene '{SceneName}' not found yet");
            return false;
        }
    }

    private AssetsFileInstance? FindSceneFileByName(string sceneName)
    {
        // Search all workspace items for a file matching the scene name
        foreach (var rootItem in Workspace.RootItems)
        {
            var result = FindSceneInItem(rootItem, sceneName);
            if (result != null) return result;
        }
        return null;
    }

    private AssetsFileInstance? FindSceneInItem(WorkspaceItem item, string sceneName)
    {
        // Check if this item matches the scene name
        if (item.ObjectType == WorkspaceItemType.AssetsFile && item.Object is AssetsFileInstance fileInst)
        {
            var name = item.Name.ToLowerInvariant();
            var searchName = sceneName.ToLowerInvariant();

            // Match by exact name, name without extension, or if the file name contains the scene name
            if (name == searchName ||
                name == searchName + ".assets" ||
                name.Contains(searchName))
            {
                return fileInst;
            }
        }

        // Search children (e.g., assets files inside bundle files)
        foreach (var child in item.Children)
        {
            var result = FindSceneInItem(child, sceneName);
            if (result != null) return result;
        }

        return null;
    }

    private void OnWorkspaceClosing(object recipient, WorkspaceClosingMessage message)
    {
        Log(SceneLogLevel.Info, "Workspace closing - clearing scene data");
        SceneData = null;
        SelectedObject = null;
        IsSceneLoaded = false;
        StatusText = "No scene loaded. Load a scene or open a file.";
        _currentFileInsts = null;
        _hasAutoLoaded = false;
        _isAutoLoading = false;
    }

    [RelayCommand]
    private void LoadScene()
    {
        Log(SceneLogLevel.Info, "LoadScene command executed");

        // If no file is currently selected, try to find the scene by name
        if (_currentFileInsts == null || _currentFileInsts.Count == 0)
        {
            Log(SceneLogLevel.Debug, $"No file selected, searching for scene '{SceneName}'");
            var sceneFile = FindSceneFileByName(SceneName);
            if (sceneFile != null)
            {
                _currentFileInsts = new List<AssetsFileInstance> { sceneFile };
                Log(SceneLogLevel.Info, $"Found scene file by name: {sceneFile.name}");
            }
            else
            {
                // If still no file, try to get the first available file from the workspace
                var firstFile = GetFirstAvailableFile();
                if (firstFile != null)
                {
                    _currentFileInsts = new List<AssetsFileInstance> { firstFile };
                    Log(SceneLogLevel.Info, $"Using first available file: {firstFile.name}");
                }
                else
                {
                    StatusText = $"No scene file '{SceneName}' found. Open a file or bundle first.";
                    Log(SceneLogLevel.Error, $"No scene file '{SceneName}' found and no files in workspace");
                    return;
                }
            }
        }

        StatusText = "Loading scene...";
        Log(SceneLogLevel.Info, $"Loading scene from {_currentFileInsts.Count} file(s)...");

        try
        {
            var sceneData = new SceneData(Workspace, msg => Log(SceneLogLevel.Debug, msg));

            // Load from first file for now
            var fileInst = _currentFileInsts[0];
            Log(SceneLogLevel.Debug, $"Loading from file: {fileInst.name}");
            sceneData.LoadFromFile(fileInst);

            SceneData = sceneData;
            IsSceneLoaded = true;

            var objectCount = sceneData.AllObjects.Count;
            var meshCount = sceneData.AllObjects.Count(o => o.HasMesh);
            var texturedCount = sceneData.AllObjects.Count(o => o.HasTexture);

            StatusText = $"Loaded {objectCount} objects ({meshCount} with meshes, {texturedCount} with textures)";
            Log(SceneLogLevel.Info, $"Scene loaded successfully: {objectCount} objects, {meshCount} meshes, {texturedCount} textured");
        }
        catch (Exception ex)
        {
            StatusText = $"Error loading scene: {ex.Message}";
            Log(SceneLogLevel.Error, $"Error loading scene: {ex.Message}");
            Log(SceneLogLevel.Debug, $"Stack trace: {ex.StackTrace}");
            IsSceneLoaded = false;
        }
    }

    private AssetsFileInstance? GetFirstAvailableFile()
    {
        foreach (var rootItem in Workspace.RootItems)
        {
            var file = GetFirstFileInItem(rootItem);
            if (file != null) return file;
        }
        return null;
    }

    private AssetsFileInstance? GetFirstFileInItem(WorkspaceItem item)
    {
        if (item.ObjectType == WorkspaceItemType.AssetsFile && item.Object is AssetsFileInstance fileInst)
        {
            return fileInst;
        }

        foreach (var child in item.Children)
        {
            var file = GetFirstFileInItem(child);
            if (file != null) return file;
        }

        return null;
    }

    [RelayCommand]
    private void ResetCamera()
    {
        Log(SceneLogLevel.Info, "Resetting camera to default position");
        ResetCameraAction?.Invoke();
        StatusText = "Camera reset to default position.";
    }

    public void OnObjectSelected(SceneObject? obj)
    {
        SelectedObject = obj;

        if (obj != null)
        {
            StatusText = $"Selected: {obj.Name} (PathId: {obj.PathId})";
            Log(SceneLogLevel.Debug, $"Selected object: {obj.Name} (PathId: {obj.PathId})");

            // Send message to select corresponding asset in inspector
            if (obj.GameObjectAsset != null)
            {
                WeakReferenceMessenger.Default.Send(new AssetsSelectedMessage([obj.GameObjectAsset]));
            }
        }
        else
        {
            StatusText = IsSceneLoaded
                ? "No object selected. Click on an object to select it."
                : "No scene loaded.";
        }
    }
}

/// <summary>
/// Log level for scene operations
/// </summary>
public enum SceneLogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

/// <summary>
/// A single log entry for scene operations
/// </summary>
public class SceneLogEntry
{
    public DateTime Timestamp { get; }
    public SceneLogLevel Level { get; }
    public string Message { get; }

    public SceneLogEntry(SceneLogLevel level, string message)
    {
        Timestamp = DateTime.Now;
        Level = level;
        Message = message;
    }

    public string LevelString => Level switch
    {
        SceneLogLevel.Debug => "DEBUG",
        SceneLogLevel.Info => "INFO",
        SceneLogLevel.Warning => "WARN",
        SceneLogLevel.Error => "ERROR",
        _ => "UNKNOWN"
    };

    public string FormattedMessage => $"[{Timestamp:HH:mm:ss}] [{LevelString}] {Message}";
}

/// <summary>
/// Converter for log level to color for display in the log view
/// </summary>
public class LogLevelToColorConverter : Avalonia.Data.Converters.IMultiValueConverter
{
    public static readonly LogLevelToColorConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        if (values.Count > 0 && values[0] is SceneLogLevel level)
        {
            return level switch
            {
                SceneLogLevel.Debug => Avalonia.Media.Brushes.Gray,
                SceneLogLevel.Info => Avalonia.Media.Brushes.LightGray,
                SceneLogLevel.Warning => Avalonia.Media.Brushes.Yellow,
                SceneLogLevel.Error => Avalonia.Media.Brushes.Red,
                _ => Avalonia.Media.Brushes.White
            };
        }
        return Avalonia.Media.Brushes.White;
    }
}
