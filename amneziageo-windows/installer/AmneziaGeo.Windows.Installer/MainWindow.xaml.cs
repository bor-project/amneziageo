using System.Windows;

namespace AmneziaGeo.Windows.Installer;

/// <summary>
/// Installer window.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// ctor
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is InstallerViewModel vm)
            {
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(InstallerViewModel.LegalBlocks))
                    {
                        TermsScroll.ScrollToTop();
                    }
                };
            }
        };
    }
}
