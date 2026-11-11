using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmneziaGeo.Ui.Controls;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Views;

internal sealed partial class TransportProxiesView : UserControl
{
    private TransportProxiesViewModel? _vm;
    private Vector _catalogueOffset;

    /// <summary>
    /// ctor
    /// </summary>
    public TransportProxiesView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => PushWidth(e.NewSize.Width);
        DataContextChanged += (_, _) => Bind();
    }

    private void PushWidth(double width)
    {
        if (_vm is not null)
        {
            _vm.PaneWidth = width;
        }
    }

    private void Bind()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnModelChanged;
            _vm.PropertyChanging -= OnModelChanging;
        }

        _vm = DataContext as TransportProxiesViewModel;
        if (_vm is not null)
        {
            _vm.PropertyChanged += OnModelChanged;
            _vm.PropertyChanging += OnModelChanging;
            PushWidth(Bounds.Width);
        }
    }

    private void OnModelChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (e.PropertyName is nameof(TransportProxiesViewModel.Editor) or nameof(TransportProxiesViewModel.IsLoading)
            && _vm?.IsCatalogue == true)
        {
            _catalogueOffset = CatalogueScroll.Offset;
        }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransportProxiesViewModel.ShowEditor))
        {
            return;
        }

        if (_vm?.ShowEditor == true)
        {
            Dispatcher.UIThread.Post(() =>
            {
                EditorScroll.Offset = default;
                PaneFocus.FocusFirst(EditorScroll);
            }, DispatcherPriority.Loaded);
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            CatalogueScroll.Offset = _catalogueOffset;
            var row = _vm?.Items.FirstOrDefault(one => one.Reference == _vm.ReturnReference);
            var index = row is null ? -1 : _vm!.Items.IndexOf(row);
            var container = index < 0 ? null : Cards.ContainerFromIndex(index) as Control;
            if (container is not null)
            {
                PaneFocus.FocusFirst(container);
            }
            else
            {
                AddButton.Focus(UiPlatform.IsTelevision ? NavigationMethod.Directional : NavigationMethod.Unspecified);
            }
        }, DispatcherPriority.Loaded);
    }

    private void OnBodyKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Up || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Visual focused
            || PaneFocus.HasNeighbour(Body, focused, NavigationDirection.Up))
        {
            return;
        }

        e.Handled = PaneFocus.FocusFirst(CatalogueHeader);
    }
}
