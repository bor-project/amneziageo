using AmneziaGeo.Ipc;
using AmneziaGeo.Ui.Services;
using AmneziaGeo.Ui.ViewModels;

namespace AmneziaGeo.Ui.Fleet;

/// <summary>
/// Журнал, пока машина держит несколько туннелей: замер идёт через выбранный сервер, и на время прогона
/// машина достаётся ему.
/// </summary>
internal sealed class FleetLogsViewModel : LogsViewModel
{
    private readonly MainWindowViewModel _shell;

    /// <summary>
    /// ctor
    /// </summary>
    public FleetLogsViewModel(MainWindowViewModel host, IAgentConnection connection, UiPreferences prefs)
        : base(host, connection, prefs)
    {
        _shell = host;
    }

    /// <inheritdoc/>
    protected override async Task BeginProbeAsync()
    {
        if (_shell.HomeFleet is not { } home)
        {
            return;
        }

        home.RolesLocked = true;
        // Машину занимает всякий прогон через туннель: и «Авто» по правилам, и принудительный уедут
        // носителем, а отчёт назовёт выбранный сервер. Мимо туннеля машина не нужна.
        if (ProbePath != ProbePaths.Bypass)
        {
            await home.TakePrimaryAsync();
        }
    }

    /// <inheritdoc/>
    protected override async Task EndProbeAsync()
    {
        if (_shell.HomeFleet is not { } home)
        {
            return;
        }

        await home.ReturnPrimaryAsync();
        home.RolesLocked = false;
    }
}
