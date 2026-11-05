using AmneziaGeo.Decl;

namespace AmneziaGeo.Dal;

public sealed partial class SqliteStateStore
{
    /// <inheritdoc/>
    public async Task<TransportProxyState> GetTransportProxyStateAsync(CancellationToken ct = default)
    {
        var lease = await LeaseAsync(ct).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            var command = lease.Connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "SELECT revision, payload FROM transport_proxy_state WHERE id = 1;";
                var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                await using (reader.ConfigureAwait(false))
                {
                    if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        return TransportProxyState.Empty;
                    }

                    var state = TransportProxyState.Parse(reader.GetString(1));
                    if (state.Revision != reader.GetInt64(0))
                    {
                        throw new InvalidDataException("The transport proxy catalog revision does not match its row.");
                    }

                    return state;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<bool> TrySaveTransportProxyStateAsync(TransportProxyState state, long expectedRevision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (expectedRevision < 0 || expectedRevision == long.MaxValue || state.Revision != expectedRevision + 1)
        {
            throw new ArgumentException("The transport proxy revision must advance by one.", nameof(state));
        }

        var payload = state.ToPayload();
        _ = TransportProxyState.Parse(payload);
        var lease = await LeaseAsync(ct).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            var command = lease.Connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = expectedRevision == 0
                    ? """
                      INSERT INTO transport_proxy_state (id, revision, payload)
                      VALUES (1, $revision, $payload)
                      ON CONFLICT(id) DO UPDATE SET
                          revision = excluded.revision,
                          payload = excluded.payload
                      WHERE transport_proxy_state.revision = $expected;
                      """
                    : """
                      UPDATE transport_proxy_state
                      SET revision = $revision, payload = $payload
                      WHERE id = 1 AND revision = $expected;
                      """;
                command.Parameters.AddWithValue("$revision", state.Revision);
                command.Parameters.AddWithValue("$payload", payload);
                command.Parameters.AddWithValue("$expected", expectedRevision);
                return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
            }
        }
    }
}
