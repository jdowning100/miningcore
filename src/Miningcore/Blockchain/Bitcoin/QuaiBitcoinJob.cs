using System.Globalization;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using NBitcoin.DataEncoders;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// Bitcoin job implementation for Quai's custom getBlockTemplate format.
/// Quai provides pre-built coinbase parts (coinb1/coinb2) and pre-computed merkle branches,
/// rather than the full transaction list.
/// </summary>
public class QuaiBitcoinJob
{
    protected IHashAlgorithm blockHasher;
    protected IMasterClock clock;
    protected IHashAlgorithm coinbaseHasher;
    protected double shareMultiplier;
    protected IHashAlgorithm headerHasher;

    protected BitcoinTemplate coin;
    private BitcoinTemplate.BitcoinNetworkParams networkParams;
    protected readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    protected uint256 blockTargetValue;

    // Quai-specific: pre-built coinbase parts
    protected byte[] coinb1;
    protected byte[] coinb2;
    protected string coinb1Hex;
    protected string coinb2Hex;
    protected int extranonce1Length;
    protected int extranonce2Length;

    // Merkle tree with pre-computed branches
    protected MerkleTree mt;
    protected string[] merkleBranchesHex;

    // Job params for mining.notify
    protected object[] jobParams;
    protected string previousBlockHashReversedHex;

    // Quai-specific fields for stale detection
    public long QuaiHeight { get; protected set; }
    public string QuaiRoot { get; protected set; }

    public QuaiBlockTemplate BlockTemplate { get; protected set; }
    public double Difficulty { get; protected set; }
    public string JobId { get; protected set; }

    public void Init(QuaiBlockTemplate blockTemplate, string jobId,
        PoolConfig pc, ClusterConfig cc, IMasterClock clock,
        BitcoinTemplate.BitcoinNetworkParams networkParams, double shareMultiplier,
        IHashAlgorithm coinbaseHasher, IHashAlgorithm headerHasher, IHashAlgorithm blockHasher)
    {
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(pc);
        Contract.RequiresNonNull(cc);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(coinbaseHasher);
        Contract.RequiresNonNull(headerHasher);
        Contract.RequiresNonNull(blockHasher);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));

        coin = pc.Template.As<BitcoinTemplate>();
        this.networkParams = networkParams;
        this.clock = clock;
        BlockTemplate = blockTemplate;
        JobId = jobId;
        this.shareMultiplier = shareMultiplier;
        this.coinbaseHasher = coinbaseHasher;
        this.headerHasher = headerHasher;
        this.blockHasher = blockHasher;

        // Store Quai-specific fields
        QuaiHeight = blockTemplate.QuaiHeight;
        QuaiRoot = blockTemplate.QuaiRoot;

        // Parse extranonce lengths (with defaults)
        extranonce1Length = blockTemplate.Extranonce1Length > 0 ? blockTemplate.Extranonce1Length : 4;
        extranonce2Length = blockTemplate.Extranonce2Length > 0 ? blockTemplate.Extranonce2Length : 8;

        // Store pre-built coinbase parts (strip 0x prefix only, preserve leading zeros)
        coinb1Hex = blockTemplate.Coinb1?.StripHexPrefix() ?? string.Empty;
        coinb2Hex = blockTemplate.Coinb2?.StripHexPrefix() ?? string.Empty;
        coinb1 = coinb1Hex.HexToByteArray();
        coinb2 = coinb2Hex.HexToByteArray();

        // IMPORTANT: Quai requires using the 'target' field from RPC for block validation.
        // The 'bits' field is ONLY used for placing in the block header, NOT for difficulty comparison.
        // This differs from standard Bitcoin where bits can be used to derive the target.
        if(string.IsNullOrEmpty(blockTemplate.Target))
            throw new ArgumentException("Quai block template must include 'target' field for difficulty validation");

        var targetHex = blockTemplate.Target.StripHexPrefix();
        blockTargetValue = new uint256(targetHex);
        var targetBigInt = System.Numerics.BigInteger.Parse("0" + targetHex, NumberStyles.HexNumber);
        Difficulty = (double) new BigRational(BitcoinConstants.Diff1, targetBigInt) * shareMultiplier;

        // Process merkle branches: convert from BE (RPC) to LE (internal)
        var branches = blockTemplate.MerkleBranch ?? Array.Empty<string>();
        var branchesLE = branches
            .Select(hex => hex.StripHexPrefix().HexToByteArray().ReverseInPlace())
            .ToList();

        mt = new MerkleTree(branchesLE, precomputed: true);

        merkleBranchesHex = branchesLE
            .Select(b => b.ToHexString())
            .ToArray();

        // Build previousBlockHashReversedHex using ReverseByteOrder (swap256 equivalent)
        var prevHashHex = blockTemplate.PreviousBlockHash?.StripHexPrefix() ?? string.Empty;
        previousBlockHashReversedHex = prevHashHex
            .HexToByteArray()
            .ReverseByteOrder()
            .ToHexString();

        // Build job params for mining.notify
        // [job_id, prevhash, coinb1, coinb2, merkle_branches, version, nbits, ntime, clean_jobs]
        jobParams = new object[]
        {
            JobId,
            previousBlockHashReversedHex,
            coinb1Hex,
            coinb2Hex,
            merkleBranchesHex,
            ((uint)blockTemplate.Version).ToStringHex8(),
            blockTemplate.Bits?.StripHexPrefix() ?? string.Empty,
            ((uint)blockTemplate.CurTime).ToStringHex8(),
            false
        };
    }

    public object GetJobParams(bool isNew)
    {
        jobParams[^1] = isNew;
        return jobParams;
    }

    /// <summary>
    /// Builds the complete coinbase transaction from pre-built parts and extranonces.
    /// Format: coinb1 + extranonce1 + extranonce2 + coinb2
    /// </summary>
    protected virtual byte[] SerializeCoinbase(string extraNonce1, string extraNonce2)
    {
        var extraNonce1Bytes = extraNonce1.HexToByteArray();
        var extraNonce2Bytes = extraNonce2.HexToByteArray();

        using var stream = new MemoryStream();
        stream.Write(coinb1);
        stream.Write(extraNonce1Bytes);
        stream.Write(extraNonce2Bytes);
        stream.Write(coinb2);
        return stream.ToArray();
    }

    /// <summary>
    /// Serializes the block header using NBitcoin.
    /// </summary>
    protected byte[] SerializeHeader(Span<byte> coinbaseHash, uint nTime, uint nonce, uint? versionMask, uint? versionBits)
    {
        // Build merkle root from coinbase hash and pre-computed branches
        var merkleRoot = mt.WithFirst(coinbaseHash.ToArray());

        // Build version (with optional ASICBoost version rolling)
        var version = BlockTemplate.Version;
        if(versionMask.HasValue && versionBits.HasValue)
            version = (version & ~versionMask.Value) | (versionBits.Value & versionMask.Value);

        var prevHashHex = BlockTemplate.PreviousBlockHash?.StripHexPrefix() ?? string.Empty;

#pragma warning disable 618
        var blockHeader = new BlockHeader
#pragma warning restore 618
        {
            Version = unchecked((int) version),
            Bits = new Target(Encoders.Hex.DecodeData(BlockTemplate.Bits.StripHexPrefix())),
            HashPrevBlock = uint256.Parse(prevHashHex),
            HashMerkleRoot = new uint256(merkleRoot),
            BlockTime = DateTimeOffset.FromUnixTimeSeconds(nTime),
            Nonce = nonce
        };

        return blockHeader.ToBytes();
    }

    /// <summary>
    /// Serializes the block for submission.
    /// For Quai: header + varint(1) + coinbase (no other transactions)
    /// </summary>
    protected virtual byte[] SerializeBlock(byte[] header, byte[] coinbase)
    {
        using var stream = new MemoryStream();
        var bs = new BitcoinStream(stream, true);

        // Write 80-byte header
        bs.ReadWrite(header);

        // Write transaction count (just coinbase = 1)
        uint txCount = 1;
        bs.ReadWriteAsVarInt(ref txCount);

        // Write coinbase transaction
        bs.ReadWrite(coinbase);

        return stream.ToArray();
    }

    public virtual (Share Share, string BlockHex, string PowHash, string HeaderSha256d, double BlockPercent) ProcessShare(StratumConnection worker,
        string extraNonce2, string nTime, string nonce, string versionBits = null)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(extraNonce2));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nTime));
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Validate extranonce2 length
        if(extraNonce2.Length / 2 != extranonce2Length)
            throw new StratumException(StratumError.Other, $"incorrect extranonce2 length: expected {extranonce2Length * 2} hex chars");

        // Parse nTime
        var nTimeInt = uint.Parse(nTime, NumberStyles.HexNumber);

        // Parse nonce
        var nonceInt = uint.Parse(nonce, NumberStyles.HexNumber);

        // Parse version bits for ASICBoost
        uint? versionBitsInt = null;
        if(context.VersionRollingMask.HasValue && versionBits != null)
        {
            versionBitsInt = uint.Parse(versionBits, NumberStyles.HexNumber);

            // Validate version bits are within allowed mask
            if((versionBitsInt.Value & ~context.VersionRollingMask.Value) != 0)
                throw new StratumException(StratumError.Other, "version-rolling mask violation");
        }

        // Check for duplicate submission
        var extraNonce1 = context.ExtraNonce1;
        if(!RegisterSubmit(extraNonce1, extraNonce2, nTime, nonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        // Build coinbase
        var coinbase = SerializeCoinbase(extraNonce1, extraNonce2);
        Span<byte> coinbaseHash = stackalloc byte[32];
        coinbaseHasher.Digest(coinbase, coinbaseHash);

        // Build header
        var headerBytes = SerializeHeader(coinbaseHash, nTimeInt, nonceInt, context.VersionRollingMask, versionBitsInt);

        // Calculate SHA256d of header (always useful for debugging, same as Bitcoin header hash)
        Span<byte> headerSha256d = stackalloc byte[32];
        coinbaseHasher.Digest(headerBytes, headerSha256d);  // coinbaseHasher is SHA256d

        // Calculate PoW hash (Scrypt or SHA256d depending on algorithm)
        Span<byte> powHash = stackalloc byte[32];
        headerHasher.Digest(headerBytes, powHash, (ulong) nTimeInt, null, coin, networkParams);
        var powHashValue = new uint256(powHash);

        // Calculate share difficulty using PoW hash
        var shareDiff = (double) new BigRational(BitcoinConstants.Diff1, powHash.ToBigInteger()) * shareMultiplier;
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // Check if share meets block target
        var isBlockCandidate = powHashValue <= blockTargetValue;

        // Check if share meets pool difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // Check previous difficulty (for vardiff retarget grace period)
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");

                stratumDifficulty = context.PreviousDifficulty.Value;
            }
            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share ({shareDiff})");
        }

        // Calculate block percentage (how close to being a valid block)
        // shareDiff / networkDiff * 100 = percentage of network difficulty
        var blockPercent = shareDiff / Difficulty * 100.0;

        // Display hashes in big-endian (human-readable with leading zeros visible for valid PoW)
        var powHashReversed = new byte[32];
        var headerSha256dReversed = new byte[32];
        for(var i = 0; i < 32; i++)
        {
            powHashReversed[i] = powHash[31 - i];
            headerSha256dReversed[i] = headerSha256d[31 - i];
        }
        var powHashHex = powHashReversed.ToHexString();
        var headerSha256dHex = headerSha256dReversed.ToHexString();

        // Build share result
        var result = new Share
        {
            BlockHeight = BlockTemplate.Height,
            NetworkDifficulty = Difficulty,
            Difficulty = stratumDifficulty / shareMultiplier
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            // Compute block hash for recording (no extra reversal - matches BitcoinJob)
            Span<byte> blockHash = stackalloc byte[32];
            blockHasher.Digest(headerBytes, blockHash, (ulong) nTimeInt, null, coin, networkParams);
            result.BlockHash = blockHash.ToHexString();

            // Serialize block for submission
            var blockBytes = SerializeBlock(headerBytes, coinbase);
            var blockHex = blockBytes.ToHexString();

            return (result, blockHex, powHashHex, headerSha256dHex, blockPercent);
        }

        return (result, null, powHashHex, headerSha256dHex, blockPercent);
    }

    protected bool RegisterSubmit(string extraNonce1, string extraNonce2, string nTime, string nonce)
    {
        var key = $"{extraNonce1}:{extraNonce2}:{nTime}:{nonce}";
        return submissions.TryAdd(key, true);
    }

    /// <summary>
    /// Gets the configured extranonce2 length for this job.
    /// </summary>
    public int Extranonce2Length => extranonce2Length;

    /// <summary>
    /// Gets the configured extranonce1 length for this job.
    /// </summary>
    public int Extranonce1Length => extranonce1Length;
}
