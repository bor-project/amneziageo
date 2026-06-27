using System;
using AmneziaGeo.Ipc;
using AmneziaGeo.Windows.Ui.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AmneziaGeo.Windows.Ui.ViewModels;

/// <summary>
/// View model for the manual config editor dialog. In "new" mode it is a comfortable text surface whose
/// text the caller imports through the normal add flow; in "edit" mode it loads an existing config's
/// wg-quick text and overwrites it in place (edit-config) on save, preserving memberships/geo/routing.
/// </summary>
internal sealed partial class ConfigEditorViewModel : ViewModelBase
{
    private readonly AgentConnection? _connection;

    [ObservableProperty]
    private string _text = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>ctor for a new manual config (no persistence; the caller imports the returned text).</summary>
    public ConfigEditorViewModel()
    {
    }

    /// <summary>ctor for editing an existing config in place (loaded via LoadAsync, saved via edit-config).</summary>
    public ConfigEditorViewModel(AgentConnection connection, string editName)
    {
        _connection = connection;
        EditName = editName;
    }

    /// <summary>The config being edited, or null when composing a brand-new one.</summary>
    public string? EditName { get; }

    /// <summary>Whether this is a fresh manual config rather than an edit of an existing one.</summary>
    public bool IsNew => EditName is null;

    /// <summary>Dialog title.</summary>
    public string Title => IsNew ? "Конфигурация вручную" : $"Редактирование: {EditName}";

    /// <summary>
    /// Loads the existing config's wg-quick text (edit mode only).
    /// </summary>
    public async Task LoadAsync()
    {
        if (EditName is null || _connection is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpGetConfig, [EditName]));
            if (ack.Ok)
            {
                Text = ack.Message;
            }
            else
            {
                StatusMessage = ack.Message;
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Validates (and, in edit mode, persists) the config. Returns whether the dialog may close OK:
    /// new mode returns true once the text looks valid (the caller imports it); edit mode overwrites the
    /// existing config in place and returns whether the agent accepted it.
    /// </summary>
    public async Task<bool> SaveAsync()
    {
        var text = Text.Trim();
        if (text.Length == 0
            || !text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase))
        {
            StatusMessage = "Не похоже на конфигурацию WireGuard/AmneziaWG (нужны [Interface] и [Peer]).";
            return false;
        }

        if (IsNew || _connection is null)
        {
            return true;
        }

        IsBusy = true;
        try
        {
            var ack = await _connection.SendCommandAsync(new IpcCommand(IpcContract.OpEditConfig, [EditName!, text]));
            if (!ack.Ok)
            {
                StatusMessage = ack.Message;
                return false;
            }

            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
