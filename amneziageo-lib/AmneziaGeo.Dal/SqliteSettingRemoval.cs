namespace AmneziaGeo.Dal;

public sealed partial class SqliteStateStore
{
    /// <inheritdoc/>
    public async Task RemoveSettingAsync(string key, CancellationToken ct = default)
    {
        var lease = await LeaseAsync(ct).ConfigureAwait(false);
        await using (lease.ConfigureAwait(false))
        {
            var command = lease.Connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = "DELETE FROM settings WHERE key = $key;";
                command.Parameters.AddWithValue("$key", key);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
    }
}
