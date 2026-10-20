using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using AmneziaGeo.Localization;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Controls;

/// <summary>
/// Надпись главного экрана о новой ревизии подписки: нажатие предлагает обновить подписки или пропустить её.
/// </summary>
internal sealed partial class ConfigUpdateHint : UserControl
{
    /// <summary>
    /// ctor
    /// </summary>
    public ConfigUpdateHint()
    {
        InitializeComponent();
    }

    // Нажатие открывает шторку на телефоне и ТВ, меню на десктопе.
    private void OnLinkClick(object? sender, RoutedEventArgs e)
    {
        if (!UiPlatform.UsesActionSheets)
        {
            FlyoutBase.ShowAttachedFlyout(LinkPart);
            return;
        }

        if (DataContext is not ConnectionViewModel vm)
        {
            return;
        }

        var update = vm.UpdateConfigsCommand;
        var skip = vm.SkipConfigUpdateCommand;
        vm.Sheet.Show(
            Loc.Instance.Get("Main_ConfigUpdateAvailable"),
            string.Empty,
            [
                new ActionOption(Loc.Instance.Get("Main_ConfigUpdateApply"), Glyphs.Refresh, () => update.Execute(null)),
                new ActionOption(Loc.Instance.Get("Main_ConfigUpdateSkip"), Glyphs.Close, () => skip.Execute(null)),
            ]);
    }

    // Пункт меню перечитывает подписки.
    private void OnUpdateClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm)
        {
            vm.UpdateConfigsCommand.Execute(null);
        }
    }

    // Пункт меню прячет надпись до следующей ревизии.
    private void OnSkipClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is ConnectionViewModel vm)
        {
            vm.SkipConfigUpdateCommand.Execute(null);
        }
    }
}
