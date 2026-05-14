using System.Globalization;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Util;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// Job descriptor for a Pearl mining pool. Substantially simpler than the Bitcoin /
/// Quai variants because:
///
///   - The C# pool never builds a header, coinbase, or merkle root. The
///     pearl-pool-service serializes the incomplete header server-side and
///     constructs the final block (incl. STARK proof) on block hits.
///   - There is no hashing the pool can do — share verification is delegated to
///     the sidecar via HTTP.
///   - The mining.submit payload is just the base64-encoded PlainProof bytes.
///
/// The job therefore holds just enough state to (a) emit a <c>mining.notify</c>
/// frame compliant with the <c>pearl/v1</c> Stratum extension and (b) hand the
/// associated incomplete-header bytes back to the manager when a share lands.
/// </summary>
public class PearlBitcoinJob
{
    public string JobId { get; private set; }
    public long Height { get; private set; }
    public string PreviousBlockHashHex { get; private set; }
    public string IncompleteHeaderB64 { get; private set; }
    public string IncompleteHeaderHex { get; private set; }
    public string BlockNBitsHex { get; private set; }
    public string BlockTargetHex { get; private set; }
    public long CurTime { get; private set; }

    public PearlMiningConfig MiningConfig { get; private set; }
    public PearlBlockTemplate Template { get; private set; }

    /// <summary>
    /// Block-target difficulty as a Bitcoin-style "diff1-scaled" share difficulty,
    /// used for pool stats and the "block percent" metric on each accepted share.
    /// </summary>
    public double BlockDifficulty { get; private set; }

    public void Init(PearlBlockTemplate template, string jobId)
    {
        Contract.RequiresNonNull(template);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(template.IncompleteHeaderB64),
            "PearlBlockTemplate must include incomplete_header_b64");
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(template.BlockNBitsHex),
            "PearlBlockTemplate must include block_nbits_hex");

        Template = template;
        JobId = jobId;
        Height = template.Height;
        PreviousBlockHashHex = template.PreviousBlockHashHex ?? string.Empty;
        IncompleteHeaderB64 = template.IncompleteHeaderB64;
        IncompleteHeaderHex = Convert.ToHexString(Convert.FromBase64String(template.IncompleteHeaderB64))
            .ToLowerInvariant();
        BlockNBitsHex = template.BlockNBitsHex.ToLowerInvariant();
        BlockTargetHex = template.BlockTargetHex ?? string.Empty;
        CurTime = template.CurTime;
        MiningConfig = template.MiningConfig;

        // Compute the block-target-equivalent share difficulty (diff1 / target).
        // Mirrors the Bitcoin-family math; used only for stats and IsBlockCandidate
        // gating, not for cryptographic verification (which lives in the sidecar).
        var targetBig = System.Numerics.BigInteger.Parse(
            "0" + BlockTargetHex, NumberStyles.HexNumber);
        if(targetBig > 0)
            BlockDifficulty = (double) new BigRational(BitcoinConstants.Diff1, targetBig);
    }

    /// <summary>
    /// Build the <c>mining.notify</c> param array for this job.
    /// Spec (<c>pearl/v1</c>):
    /// <code>
    /// [ job_id, prev_hash_hex, incomplete_header_hex,
    ///   height, ntime_hex, share_nbits_hex, clean_jobs ]
    /// </code>
    /// </summary>
    public object[] GetJobParams(string shareNBitsHex, bool cleanJobs)
    {
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(shareNBitsHex),
            "share_nbits_hex is required");

        return new object[]
        {
            JobId,
            PreviousBlockHashHex,
            IncompleteHeaderHex,
            Height,
            ((uint) CurTime).ToString("x8"),
            shareNBitsHex.ToLowerInvariant(),
            cleanJobs,
        };
    }

    /// <summary>
    /// Build the one-shot <c>pearl.set_mining_params</c> notification body
    /// that's sent once after <c>mining.subscribe</c> so a miner can configure
    /// its kernel before the first job arrives.
    /// </summary>
    public object GetMiningParamsNotification()
    {
        return new
        {
            m = MiningConfig.M,
            n = MiningConfig.N,
            k = MiningConfig.K,
            rank = MiningConfig.Rank,
            rows_pattern = MiningConfig.RowsPattern,
            cols_pattern = MiningConfig.ColsPattern,
            mma_type = MiningConfig.MmaType,
        };
    }
}
