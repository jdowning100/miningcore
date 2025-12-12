using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

/// <summary>
/// Response from Quai's quai_getBlockTemplate RPC.
/// Unlike standard Bitcoin getBlockTemplate, this returns pre-built coinbase parts
/// and pre-computed merkle branches.
/// </summary>
public class QuaiBlockTemplate
{
    /// <summary>
    /// Compressed target of next block (big-endian hex)
    /// </summary>
    public string Bits { get; set; }

    /// <summary>
    /// Coinbase transaction part 1 (before extranonces)
    /// </summary>
    public string Coinb1 { get; set; }

    /// <summary>
    /// Coinbase transaction part 2 (after extranonces)
    /// </summary>
    public string Coinb2 { get; set; }

    /// <summary>
    /// Length of auxiliary data that can be added to coinbase
    /// </summary>
    [JsonProperty("coinbaseAuxExtraBytesLength")]
    public int CoinbaseAuxExtraBytesLength { get; set; }

    /// <summary>
    /// Current timestamp in seconds since epoch
    /// </summary>
    [JsonProperty("curtime")]
    public long CurTime { get; set; }

    /// <summary>
    /// Length of extranonce1 in bytes
    /// </summary>
    [JsonProperty("extranonce1Length")]
    public int Extranonce1Length { get; set; }

    /// <summary>
    /// Length of extranonce2 in bytes
    /// </summary>
    [JsonProperty("extranonce2Length")]
    public int Extranonce2Length { get; set; }

    /// <summary>
    /// The height of the next block
    /// </summary>
    public long Height { get; set; }

    /// <summary>
    /// Pre-computed merkle branches (big-endian hex, must be reversed to LE for use)
    /// </summary>
    [JsonProperty("merklebranch")]
    public string[] MerkleBranch { get; set; }

    /// <summary>
    /// Pre-computed merkle root (for reference/validation)
    /// </summary>
    [JsonProperty("merkleroot")]
    public string MerkleRoot { get; set; }

    /// <summary>
    /// Minimum timestamp allowed
    /// </summary>
    [JsonProperty("mintime")]
    public long MinTime { get; set; }

    /// <summary>
    /// A range of valid nonces
    /// </summary>
    [JsonProperty("noncerange")]
    public string NonceRange { get; set; }

    /// <summary>
    /// The hash of current highest block (big-endian hex)
    /// </summary>
    [JsonProperty("previousblockhash")]
    public string PreviousBlockHash { get; set; }

    /// <summary>
    /// Quai-specific difficulty (for display/stats)
    /// </summary>
    [JsonProperty("quaidifficulty")]
    public long QuaiDifficulty { get; set; }

    /// <summary>
    /// Quai-specific block height (for stale detection)
    /// </summary>
    [JsonProperty("quaiheight")]
    public long QuaiHeight { get; set; }

    /// <summary>
    /// Quai-specific state root (for stale detection)
    /// </summary>
    [JsonProperty("quairoot")]
    public string QuaiRoot { get; set; }

    /// <summary>
    /// Maximum signature operations allowed
    /// </summary>
    [JsonProperty("sigoplimit")]
    public long SigOpLimit { get; set; }

    /// <summary>
    /// Maximum block size allowed
    /// </summary>
    [JsonProperty("sizelimit")]
    public long SizeLimit { get; set; }

    /// <summary>
    /// The full 32-byte hash target (big-endian hex)
    /// </summary>
    public string Target { get; set; }

    /// <summary>
    /// The preferred block version
    /// </summary>
    public uint Version { get; set; }

    /// <summary>
    /// Extension data for any additional fields
    /// </summary>
    [JsonExtensionData]
    public IDictionary<string, object> Extra { get; set; }
}

/// <summary>
/// Response from Quai's quai_submitShaBlock / quai_submitScryptBlock RPC.
/// </summary>
public class QuaiSubmitBlockResponse
{
    /// <summary>
    /// Block number (hex encoded)
    /// </summary>
    public string Number { get; set; }

    /// <summary>
    /// Block hash (hex encoded)
    /// </summary>
    public string Hash { get; set; }

    /// <summary>
    /// Submission status:
    /// 0 = Sub (work share only, not a valid block)
    /// 1 = Valid (valid work, accepted)
    /// 2 = Block (valid block, accepted and will be mined)
    /// </summary>
    public string Status { get; set; }

    /// <summary>
    /// Parses the hex status to an integer
    /// </summary>
    public int StatusValue
    {
        get
        {
            if(string.IsNullOrEmpty(Status))
                return -1;

            var hex = Status.StartsWith("0x") ? Status[2..] : Status;
            return int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var val) ? val : -1;
        }
    }

    /// <summary>
    /// Returns true if the submission was accepted (status 1 or 2)
    /// </summary>
    public bool IsAccepted => StatusValue >= 1;

    /// <summary>
    /// Returns true if this is a valid block (status 2)
    /// </summary>
    public bool IsBlock => StatusValue == 2;
}
