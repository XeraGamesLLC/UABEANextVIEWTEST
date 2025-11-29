using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using System.Linq;
using System.Threading.Tasks;
using UABEANext4.ViewModels;

namespace UABEANext4.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
#if DEBUG
        InitializeComponent(attachDevTools: true);
        //DevToolsAdblock.Attach(this, new DevToolsOptions());
#else
        InitializeComponent();
#endif
        AddHandler(DragDrop.DropEvent, Drop);

        // Set up window dragging on title bar
        var titleBar = this.FindControl<Panel>("TitleBar");
        if (titleBar != null)
        {
            titleBar.PointerPressed += TitleBar_PointerPressed;
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async Task Drop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { } files && DataContext is MainViewModel viewModel)
        {
            var fileNames = files.Select(sf => sf.TryGetLocalPath()).Where(p => p != null);
            if (fileNames is not null)
            {
                await viewModel.OpenFiles(fileNames);
            }
        }
    }
}
