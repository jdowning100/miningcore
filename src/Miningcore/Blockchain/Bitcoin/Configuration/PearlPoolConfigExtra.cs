namespace Miningcore.Blockchain.Bitcoin.Configuration;

/// <summary>
/// Pool-config extras specific to a Pearl pool. The Pearl integration delegates
/// PlainProof verification and STARK proof construction to a co-located sidecar
/// (<c>pearl-pool-service</c>) reached over HTTP.
/// </summary>
public class PearlPoolConfigExtra
{
    /// <summary>
    /// Base URL of the <c>pearl-pool-service</c> instance (e.g. "http://127.0.0.1:8341").
    /// Used for /v1/pool-template, /v1/verify-share, and /v1/prove-and-submit.
    /// </summary>
    public string PoolServiceUrl { get; set; } = "http://127.0.0.1:8341";

    /// <summary>
    /// Timeout for the hot path (/v1/verify-share) in milliseconds.
    /// Default 5 s — verify is typically &lt; 50 ms in practice.
    /// </summary>
    public int PoolServiceTimeoutMs { get; set; } = 5000;

    /// <summary>
    /// Timeout for the cold path (/v1/prove-and-submit) in milliseconds.
    /// Block STARK construction is 2–10 s on tuned hardware; allow generous headroom.
    /// </summary>
    public int PoolServiceProveTimeoutMs { get; set; } = 30000;

    /// <summary>
    /// Template polling interval in milliseconds. Matches pool-service's own
    /// pearld polling cadence (default 1000 ms).
    /// </summary>
    public int PoolTemplatePollMs { get; set; } = 1000;
}
