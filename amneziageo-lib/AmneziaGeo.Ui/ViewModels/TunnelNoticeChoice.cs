using System.Collections.ObjectModel;
using AmneziaGeo.Ipc;
using AmneziaGeo.Localization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Ui.ViewModels;

/// <summary>
/// Выбор уведомления туннеля: подробное или краткое.
/// </summary>
internal sealed partial class TunnelNoticeChoice : ObservableObject
{
    private const int Brief = 1;

    private readonly Action<string> _push;

    // Выбор ставит окно, а не пользователь.
    private bool _told;

    // Последний выбор, который стоял в списке.
    private int _kept;

    /// <summary>
    /// ctor
    /// </summary>
    public TunnelNoticeChoice(Action<string> push)
    {
        _push = push;
    }

    /// <summary>
    /// Варианты на языке окна.
    /// </summary>
    public ObservableCollection<string> Options { get; } = [Loc.Instance.Get("Background_TunnelNoticeFull"), Loc.Instance.Get("Background_TunnelNoticeBrief")];

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>
    /// Показывает режим, который назвал агент.
    /// </summary>
    public void Apply(string mode)
    {
        Tell(TunnelNoticeModes.Known(mode) == TunnelNoticeModes.Brief ? Brief : 0);
    }

    /// <summary>
    /// Перечитывает варианты на новом языке.
    /// </summary>
    public void Relabel()
    {
        var index = _kept;
        _told = true;
        try
        {
            Options[0] = Loc.Instance.Get("Background_TunnelNoticeFull");
            Options[1] = Loc.Instance.Get("Background_TunnelNoticeBrief");
            SelectedIndex = index;
        }
        finally
        {
            _told = false;
        }
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (_told)
        {
            return;
        }

        if (value < 0)
        {
            Dispatcher.UIThread.Post(() => Tell(_kept), DispatcherPriority.Background);
            return;
        }

        _kept = value;
        _push(value == Brief ? TunnelNoticeModes.Brief : TunnelNoticeModes.Full);
    }

    private void Tell(int index)
    {
        _told = true;
        try
        {
            SelectedIndex = index;
            _kept = index;
        }
        finally
        {
            _told = false;
        }
    }
}
