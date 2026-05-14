using Newtonsoft.Json;

namespace Miningcore.Blockchain.Bitcoin.DaemonResponses;

/// <summary>
/// Mining-config slice of <see cref="PearlBlockTemplate"/>. Identifies the
/// pool-wide matmul / noise / pattern shape used by every connected miner so
/// that share difficulty is comparable across workers.
/// </summary>
public class PearlMiningConfig
{
    [JsonProperty("m")] public int M { get; set; }
    [JsonProperty("n")] public int N { get; set; }
    [JsonProperty("k")] public int K { get; set; }
    [JsonProperty("rank")] public int Rank { get; set; }
    [JsonProperty("rows_pattern")] public int[] RowsPattern { get; set; } = Array.Empty<int>();
    [JsonProperty("cols_pattern")] public int[] ColsPattern { get; set; } = Array.Empty<int>();
    [JsonProperty("mma_type")] public string MmaType { get; set; } = "Int7xInt7ToInt32";
}

/// <summary>
/// Response DTO for <c>GET /v1/pool-template</c> on the pearl-pool-service.
/// Carries the chain template (from pearld) plus the pool-chosen mining config.
/// </summary>
public class PearlBlockTemplate
{
    [JsonProperty("ok")] public bool Ok { get; set; }
    [JsonProperty("reason")] public string Reason { get; set; }

    [JsonProperty("height")] public long Height { get; set; }
    [JsonProperty("previous_block_hash_hex")] public string PreviousBlockHashHex { get; set; }
    [JsonProperty("incomplete_header_b64")] public string IncompleteHeaderB64 { get; set; }
    [JsonProperty("block_nbits_hex")] public string BlockNBitsHex { get; set; }
    [JsonProperty("block_target_hex")] public string BlockTargetHex { get; set; }
    [JsonProperty("cur_time")] public long CurTime { get; set; }
    [JsonProperty("mining_config")] public PearlMiningConfig MiningConfig { get; set; }
}
