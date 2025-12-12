using Miningcore.Configuration;
using Miningcore.Nicehash.API;
using Miningcore.Time;
using Miningcore.VarDiff;

namespace Miningcore.Mining;

public class ShareStats
{
    public int ValidShares { get; set; }
    public int InvalidShares { get; set; }

    // Real-time hashrate tracking
    public DateTime? FirstShareTime { get; set; }
    public DateTime? LastShareTime { get; set; }
    public double CumulativeDifficulty { get; set; }

    /// <summary>
    /// Records a valid share for hashrate calculation
    /// </summary>
    public void RecordShare(double difficulty, DateTime timestamp)
    {
        ValidShares++;
        CumulativeDifficulty += difficulty;

        if(!FirstShareTime.HasValue)
            FirstShareTime = timestamp;

        LastShareTime = timestamp;
    }

    /// <summary>
    /// Calculates estimated hashrate based on shares submitted.
    /// Works correctly for both SHA-256 and Scrypt by using normalized internal difficulty.
    /// </summary>
    /// <returns>Hashrate in H/s, or 0 if insufficient data</returns>
    public double GetEstimatedHashrate()
    {
        if(!FirstShareTime.HasValue || !LastShareTime.HasValue || ValidShares < 2)
            return 0;

        var elapsed = (LastShareTime.Value - FirstShareTime.Value).TotalSeconds;
        if(elapsed < 1)
            return 0;

        // share.Difficulty is stored in internal units (stratumDiff / shareMultiplier)
        // This normalization handles the different diff1 values:
        //   - SHA-256: diff1 = 2^32 hashes, shareMultiplier = 1
        //   - Scrypt:  diff1 = 2^16 hashes, shareMultiplier = 65536 (2^16)
        //
        // For Scrypt: internalDiff = stratumDiff / 65536
        //   expectedHashes = internalDiff * 2^32 = stratumDiff * 2^32 / 2^16 = stratumDiff * 2^16 ✓
        //
        // For SHA-256: internalDiff = stratumDiff / 1 = stratumDiff
        //   expectedHashes = internalDiff * 2^32 = stratumDiff * 2^32 ✓
        //
        // hashrate = totalExpectedHashes / elapsed
        return CumulativeDifficulty * Math.Pow(2, 32) / elapsed;
    }
}

public class WorkerContextBase
{
    private double? pendingDifficulty;
    private string userAgent;

    public ShareStats Stats { get; set; }
    public VarDiffContext VarDiff { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastActivity { get; set; }
    public bool IsAuthorized { get; set; }
    public bool IsSubscribed { get; set; }

    /// <summary>
    /// Timestamp when the last job was sent to this worker.
    /// Used for periodic job refresh to ensure miners don't work on stale jobs.
    /// </summary>
    public DateTime LastJobSent { get; set; }

    /// <summary>
    /// Difficulty assigned to this worker, either static or updated through VarDiffManager
    /// </summary>
    public double Difficulty { get; set; }

    /// <summary>
    /// Previous difficulty assigned to this worker
    /// </summary>
    public double? PreviousDifficulty { get; set; }

    /// <summary>
    /// Usually a wallet address
    /// </summary>
    public virtual string Miner { get; set; }

    /// <summary>
    /// Arbitrary worker identififer for miners using multiple rigs
    /// </summary>
    public virtual string Worker { get; set; }

    /// <summary>
    /// UserAgent reported by Stratum
    /// </summary>
    public string UserAgent
    {
        get => userAgent;
        set
        {
            userAgent = value;

            IsNicehash = userAgent?.Contains(NicehashConstants.NicehashUA, StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    public bool IsNicehash { get; private set; }

    public void Init(double difficulty, VarDiffConfig varDiffConfig, IMasterClock clock)
    {
        Difficulty = difficulty;
        LastActivity = clock.Now;
        Created = clock.Now;
        Stats = new ShareStats();

        if(varDiffConfig != null)
        {
            VarDiff = new VarDiffContext
            {
                Created = Created,
                Config = varDiffConfig
            };
        }
    }

    public void EnqueueNewDifficulty(double difficulty)
    {
        pendingDifficulty = difficulty;
    }

    public bool HasPendingDifficulty => pendingDifficulty.HasValue;

    public bool ApplyPendingDifficulty()
    {
        if(pendingDifficulty.HasValue)
        {
            SetDifficulty(pendingDifficulty.Value);
            pendingDifficulty = null;

            return true;
        }

        return false;
    }

    public void SetDifficulty(double difficulty)
    {
        PreviousDifficulty = Difficulty;
        Difficulty = difficulty;
    }

}
