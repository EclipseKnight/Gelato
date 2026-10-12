namespace Gelato.Services;

/// <summary>
/// One native-id claim at a time. Entries imported in parallel can find the same item by its
/// IMDb id at once; each checks the saved ids under the gate, so only the first one takes it.
/// </summary>
public sealed class ClaimGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="saved">Reads the item's saved provider ids.</param>
    /// <param name="native">The native id the entry would give the item.</param>
    /// <param name="take">Gives the item the native id and saves it; called on <see cref="CatalogIdentity.ClaimResult.Take"/> only.</param>
    /// <param name="ct">Cancels the wait.</param>
    public async Task<CatalogIdentity.ClaimResult> ClaimAsync(
        Func<IReadOnlyDictionary<string, string>> saved,
        KeyValuePair<string, string> native,
        Func<Task> take,
        CancellationToken ct
    )
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = CatalogIdentity.Claim(saved(), native);
            if (result == CatalogIdentity.ClaimResult.Take)
                await take().ConfigureAwait(false);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }
}
