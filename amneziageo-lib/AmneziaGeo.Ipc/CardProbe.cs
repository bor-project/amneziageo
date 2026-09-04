using System.Net.Sockets;

namespace AmneziaGeo.Ipc;

/// <summary>
/// Замер серверов для карточек каталога: те же ноги, что у свипа, но разом по всем и без вердикта. Живёт в
/// агенте, потому что только он умеет увести замер мимо туннеля: окно этого не может ни правами, ни правилами.
/// </summary>
public static class CardProbe
{
    /// <summary>
    /// Меряет все серверы и складывает ответ агента: первой строкой путь замера, дальше строка на сервер.
    /// </summary>
    public static async Task<string> RunAsync(
        IReadOnlyList<SweepServer> servers,
        bool carriesDefault,
        Func<Socket, bool>? bypass,
        CancellationToken ct)
    {
        var rows = await Task.WhenAll(servers.Select(server => MeasureAsync(server, bypass, ct))).ConfigureAwait(false);
        var payload = new List<string>(rows.Length + 1)
        {
            Path(bypass is not null, carriesDefault),
        };

        payload.AddRange(rows.Select(row => row.ToRow()));
        return string.Join('\n', payload);
    }

    /// <summary>
    /// Строка пути: ушёл ли замер мимо туннеля и держит ли туннель дефолт.
    /// </summary>
    public static string Path(bool bypassed, bool carriesDefault)
    {
        return $"path\tbypass={(bypassed ? 1 : 0)}\tdefault={(carriesDefault ? 1 : 0)}";
    }

    // Один сервер: у прокси отвечает его фронт по TCP, у обычного туннеля - эхо на эндпоинт.
    private static async Task<SweepRow> MeasureAsync(SweepServer server, Func<Socket, bool>? bypass, CancellationToken ct)
    {
        if (server.Address is null)
        {
            return new SweepRow(server.Name, LegState.Skipped, Live: server.Live);
        }

        var leg = server.CarrierPort > 0
            ? await ChannelProbe.ConnectLegAsync(server.Name, server.Address, server.CarrierPort, bypass, ct).ConfigureAwait(false)
            : await ChannelProbe.EchoLegAsync(server.Name, server.Address, bypass, measureSize: false, ct).ConfigureAwait(false);

        return new SweepRow(server.Name, leg.State, leg.RttMs, leg.JitterMs, leg.LossPercent, server.Live);
    }
}
