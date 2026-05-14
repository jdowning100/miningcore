using System.Net.Http;
using System.Net.Http.Headers;
using System.Reactive.Linq;
using System.Text;
using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;

namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// Job manager for Pearl mining pools.
///
/// Pearl's PoW is a noisy int8 GEMM with a STARK proof of correctness. The
/// cryptographic primitives live in Rust; rather than expose them to C# via
/// FFI, this manager delegates all chain-touching work to a co-located sidecar
/// service ( <c>pearl-pool-service</c>, default :8341):
///
///   - <c>GET  /v1/pool-template</c>      template polling (replaces getBlockTemplate)
///   - <c>POST /v1/verify-share</c>       per-share PlainProof verification
///   - <c>POST /v1/prove-and-submit</c>   on block hits: build STARK, call submitblock
///
/// Consequently this class:
///   - holds no <see cref="Miningcore.Rpc.RpcClient"/> — it speaks HTTP/JSON
///     to the sidecar instead of JSON-RPC to pearld;
///   - builds no headers, coinbases, merkle roots, or hashes — those live
///     server-side in the sidecar / pearld;
///   - serves the <c>pearl/v1</c> Stratum extension (<see cref="PearlBitcoinJob"/>
///     and <see cref="PearlPool"/>) with a tiny, non-Bitcoin submit shape:
///     <c>[worker_name, job_id, plain_proof_b64]</c>.
/// </summary>
public class PearlBitcoinJobManager : JobManagerBase<PearlBitcoinJob>
{
    public PearlBitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.clock = clock;
    }

    protected readonly IMasterClock clock;
    protected PearlPoolConfigExtra extraPoolConfig;
    protected BitcoinTemplate coin;
    protected HttpClient http;
    protected string poolServiceBaseUrl;

    public int MaxActiveJobs { get; protected set; } = 64;
    protected const int MaxCachedJobs = 256;
    protected DateTime? lastJobRebroadcast;
    protected TimeSpan jobRebroadcastTimeout;

    // Job cache: allow accepting shares for slightly-stale jobs while a new
    // template propagates.
    protected readonly Dictionary<string, PearlBitcoinJob> validJobs = new();
    protected readonly Queue<string> jobIdQueue = new();
    protected readonly object workerHashrateLock = new();
    protected readonly Dictionary<string, WorkerHashrateState> workerHashrates = new();
    protected static readonly TimeSpan WorkerHashrateWindow = TimeSpan.FromMinutes(5);
    protected static readonly TimeSpan WorkerHashrateLogInterval = TimeSpan.FromSeconds(15);
    protected const double DefaultPearlM = 131072d;
    protected const double DefaultPearlN = 131072d;
    protected const double DefaultPearlK = 4096d;
    protected const double DefaultPearlDifficultyFactor = 2d * 64d * 4096d;

    /// <summary>
    /// The most recent mining config the sidecar advertised. Sent to every new
    /// Stratum connection via <c>pearl.set_mining_params</c>.
    /// </summary>
    public PearlMiningConfig CurrentMiningConfig { get; private set; }

    // ------------------------------------------------------------- API surface

    public IObservable<object> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();
    public double ShareMultiplier => coin?.ShareMultiplier ?? 1;

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        base.Configure(pc, cc);

        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<PearlPoolConfigExtra>() ?? new PearlPoolConfigExtra();
        poolServiceBaseUrl = (extraPoolConfig.PoolServiceUrl ?? "http://127.0.0.1:8341").TrimEnd('/');

        logger.Info(() => $"Pearl pool configured with pool-service at {poolServiceBaseUrl}");
    }

    public override PearlBitcoinJob GetJobForStratum()
    {
        return currentJob;
    }

    /// <summary>
    /// Validate a submitted share via the sidecar; if it also meets the block
    /// target, fire off the cold-path /v1/prove-and-submit (which constructs the
    /// STARK and submits to pearld).
    /// </summary>
    public virtual async ValueTask<Share> SubmitShareAsync(
        StratumConnection worker, object submission, double stratumDifficulty, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams || submitParams.Length < 3)
            throw new StratumException(StratumError.Other, "invalid params");

        var workerName = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var plainProofB64 = submitParams[2] as string;

        if(string.IsNullOrEmpty(workerName))
            throw new StratumException(StratumError.Other, "missing or invalid workername");
        if(string.IsNullOrEmpty(jobId))
            throw new StratumException(StratumError.Other, "missing job_id");
        if(string.IsNullOrEmpty(plainProofB64))
            throw new StratumException(StratumError.Other, "missing plain_proof_b64");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Look up the job (allow slightly-stale acceptance).
        PearlBitcoinJob job;
        lock(jobLock)
        {
            if(!validJobs.TryGetValue(jobId, out job))
                throw new StratumException(StratumError.JobNotFound, "job not found");
        }

        async Task<VerifyShareResponse> VerifyAtDifficulty(double difficulty)
        {
            var verifyReq = new
            {
                job_id = job.JobId,
                incomplete_header_b64 = job.IncompleteHeaderB64,
                plain_proof_b64 = plainProofB64,
                share_nbits_hex = DifficultyToNBitsHex(difficulty),
                worker_id = workerName,
            };
            return await PostJsonAsync<VerifyShareResponse>(
                "/v1/verify-share", verifyReq, extraPoolConfig.PoolServiceTimeoutMs, ct);
        }

        // Hot path: verify against the current assigned difficulty. If vardiff
        // retargeted while the miner was constructing/submitting a PlainProof,
        // also accept shares that still satisfy the previous difficulty. This
        // mirrors the low-difficulty grace path used by other Miningcore jobs.
        var acceptedDifficulty = stratumDifficulty;
        var verifyResp = await VerifyAtDifficulty(stratumDifficulty);
        if(verifyResp.Ok && !verifyResp.MeetsShare &&
           context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
        {
            var previousVerifyResp = await VerifyAtDifficulty(context.PreviousDifficulty.Value);
            if(previousVerifyResp.Ok && previousVerifyResp.MeetsShare)
            {
                verifyResp = previousVerifyResp;
                acceptedDifficulty = context.PreviousDifficulty.Value;
            }
        }

        if(!verifyResp.Ok)
            throw new StratumException(StratumError.Other, verifyResp.Reason ?? "verify-share failed");
        if(!verifyResp.MeetsShare)
            throw new StratumException(StratumError.LowDifficultyShare,
                $"{verifyResp.Reason ?? "share does not meet pool target"} jackpot={verifyResp.JackpotHashHex}");

        // Build the Share record. Keep Miningcore's standard normalized share
        // difficulty here: hashrate is derived from submitted share difficulty
        // and elapsed wall time, not from the miner's local attempt grid.
        var share = new Share
        {
            PoolId = poolConfig.Id,
            IpAddress = worker.RemoteEndpoint.Address.ToString(),
            Miner = context.Miner,
            Worker = context.Worker,
            UserAgent = context.UserAgent,
            Source = poolConfig.Id,
            BlockType = "pearl",
            Created = clock.Now,
            Difficulty = acceptedDifficulty / ShareMultiplier,
            NetworkDifficulty = job.BlockDifficulty,
            BlockHeight = job.Height,
            BlockHash = verifyResp.JackpotHashHex,
            IsBlockCandidate = verifyResp.MeetsBlock,
        };

        // Cold path: if it also meets the block target, ask the sidecar to
        // construct the STARK proof and submit the block.
        if(verifyResp.MeetsBlock)
        {
            logger.Info(() => $"Pearl BLOCK candidate from {context.Miner}@{context.Worker} " +
                $"job={job.JobId} height={job.Height} jackpot={verifyResp.JackpotHashHex}");

            var submitReq = new
            {
                incomplete_header_b64 = job.IncompleteHeaderB64,
                plain_proof_b64 = plainProofB64,
            };

            try
            {
                var submitResp = await PostJsonAsync<ProveAndSubmitResponse>(
                    "/v1/prove-and-submit", submitReq, extraPoolConfig.PoolServiceProveTimeoutMs, ct);

                if(submitResp.Ok && submitResp.Status == "accepted")
                {
                    logger.Info(() => $"Pool-service accepted block at height={job.Height} " +
                        $"proof_elapsed_ms={submitResp.ElapsedMs}");
                    share.TransactionConfirmationData = verifyResp.JackpotHashHex;
                    OnBlockFound();
                }
                else
                {
                    var reason = submitResp.Reason ?? submitResp.Status ?? "unknown";
                    logger.Warn(() => $"Pool-service rejected block at height={job.Height}: {reason}");
                    share.IsBlockCandidate = false;
                    share.TransactionConfirmationData = null;

                    messageBus.SendMessage(new AdminNotification("Pearl block submission failed",
                        $"Pool {poolConfig.Id} failed to submit Pearl block {job.Height}: {reason}"));
                }
            }
            catch(Exception ex)
            {
                logger.Error(ex, () => $"prove-and-submit failed for height={job.Height}");
                share.IsBlockCandidate = false;
                share.TransactionConfirmationData = null;
            }
        }

        // Record share for hashrate tracking.
        context.Stats.RecordShare(share.Difficulty, clock.Now);
        RecordWorkerHashrate(context, workerName, acceptedDifficulty / ShareMultiplier, job);

        return share;
    }

    protected virtual void RecordWorkerHashrate(BitcoinWorkerContext context, string submittedWorkerName,
        double acceptedDifficulty, PearlBitcoinJob job)
    {
        var now = clock.Now;
        var miner = !string.IsNullOrEmpty(context.Miner) ? context.Miner : submittedWorkerName;
        var workerName = context.Worker ?? string.Empty;
        var workerKey = string.IsNullOrEmpty(workerName) ? miner : $"{miner}.{workerName}";
        var expectedMacs = acceptedDifficulty * BitcoinConstants.Pow2x32;

        WorkerHashrateState state;
        lock(workerHashrateLock)
        {
            if(!workerHashrates.TryGetValue(workerKey, out state))
            {
                state = new WorkerHashrateState(now);
                workerHashrates[workerKey] = state;
            }

            state.Samples.Enqueue(new WorkerHashrateSample(now, acceptedDifficulty, expectedMacs));

            var cutoff = now - WorkerHashrateWindow;
            while(state.Samples.Count > 0 && state.Samples.Peek().Timestamp < cutoff)
                state.Samples.Dequeue();

            if(state.Samples.Count < 2 || now - state.LastLog < WorkerHashrateLogInterval)
                return;

            var first = state.Samples.Peek().Timestamp;
            var elapsed = Math.Max((now - first).TotalSeconds, 1.0d);
            state.LastLog = now;

            var totalExpectedMacs = 0d;
            foreach(var sample in state.Samples)
                totalExpectedMacs += sample.ExpectedMacs;

            var hashrate = totalExpectedMacs / elapsed;
            var avgDiff = state.Samples.Average(x => x.Difficulty);
            var hashrateThs = hashrate / 1e12d;
            var poissonRelStd = 1.0d / Math.Sqrt(state.Samples.Count);

            logger.Info(() => $"Pearl worker {workerKey} estimated hashrate={hashrateThs:F2} TH/s decimal " +
                $"window={elapsed:F1}s shares={state.Samples.Count} avg_diff={avgDiff:F3} " +
                $"work_per_diff={BitcoinConstants.Pow2x32:E3} MAC " +
                $"poisson_rel_std={poissonRelStd:P1}");
        }
    }

    public static double GetPearlMacsPerAttempt(PearlMiningConfig miningConfig)
    {
        if(miningConfig == null || miningConfig.M <= 0 || miningConfig.N <= 0 || miningConfig.K <= 0)
            return DefaultPearlM * DefaultPearlN * DefaultPearlK;

        return (double) miningConfig.M * miningConfig.N * miningConfig.K;
    }

    public static double GetPearlDifficultyFactor(PearlMiningConfig miningConfig)
    {
        if(miningConfig == null || miningConfig.K <= 0)
            return DefaultPearlDifficultyFactor;

        var rows = miningConfig.RowsPattern?.Length > 0 ? miningConfig.RowsPattern.Length : 2;
        var cols = miningConfig.ColsPattern?.Length > 0 ? miningConfig.ColsPattern.Length : 64;
        return (double) rows * cols * miningConfig.K;
    }

    protected class WorkerHashrateState
    {
        public WorkerHashrateState(DateTime now)
        {
            LastLog = now;
        }

        public DateTime LastLog { get; set; }
        public Queue<WorkerHashrateSample> Samples { get; } = new();
    }

    protected readonly struct WorkerHashrateSample
    {
        public WorkerHashrateSample(DateTime timestamp, double difficulty, double expectedMacs)
        {
            Timestamp = timestamp;
            Difficulty = difficulty;
            ExpectedMacs = expectedMacs;
        }

        public DateTime Timestamp { get; }
        public double Difficulty { get; }
        public double ExpectedMacs { get; }
    }

    // ----------------------------------------------------------- job lifecycle

    protected override void ConfigureDaemons()
    {
        http = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync($"{poolServiceBaseUrl}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    protected override Task<bool> AreDaemonsConnectedAsync(CancellationToken ct) =>
        AreDaemonsHealthyAsync(ct);

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            var template = await FetchPoolTemplateAsync(ct);
            if(template?.Ok == true && template.MiningConfig != null)
            {
                logger.Info(() => $"Pool-service ready (template height {template.Height})");
                return;
            }
            logger.Info(() => "Waiting for pool-service to expose a template ...");
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        await EnsureDaemonsSynchedAsync(ct);
        SetupJobUpdates(ct);
    }

    protected virtual void SetupJobUpdates(CancellationToken ct)
    {
        jobRebroadcastTimeout = TimeSpan.FromSeconds(Math.Max(1, poolConfig.JobRebroadcastTimeout));

        var blockFound = blockFoundSubject.Synchronize();
        var pollTimerRestart = blockFoundSubject.Synchronize();

        var triggers = new List<IObservable<(bool Force, string Via, string Data)>>
        {
            blockFound.Select(_ => (false, JobRefreshBy.BlockFound, (string) null))
        };

        var pollingInterval = extraPoolConfig.PoolTemplatePollMs > 0
            ? extraPoolConfig.PoolTemplatePollMs
            : (poolConfig.BlockRefreshInterval > 0 ? poolConfig.BlockRefreshInterval : 1000);

        triggers.Add(Observable.Timer(TimeSpan.FromMilliseconds(pollingInterval))
            .TakeUntil(pollTimerRestart)
            .Select(_ => (false, JobRefreshBy.Poll, (string) null))
            .Repeat());

        if(poolConfig.JobRebroadcastTimeout > 0)
        {
            triggers.Add(Observable.Timer(jobRebroadcastTimeout)
                .TakeUntil(pollTimerRestart)
                .Select(_ => (true, JobRefreshBy.PollRefresh, (string) null))
                .Repeat());
        }

        Jobs = Observable.Merge(triggers)
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Force, x.Via)))
            .Concat()
            .Where(x => x.IsNew || x.Force)
            .Do(x =>
            {
                if(x.IsNew)
                    hasInitialBlockTemplate = true;
            })
            .Select(_ => (object) currentJob)
            .Publish()
            .RefCount();
    }

    protected async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var template = await FetchPoolTemplateAsync(ct);
            if(template?.Ok != true)
            {
                logger.Warn(() => $"Unable to update Pearl job: {template?.Reason ?? "no template"}");
                return (false, forceUpdate);
            }

            var existing = currentJob;
            var isNewBlock = existing == null ||
                existing.Height != template.Height ||
                !string.Equals(existing.IncompleteHeaderB64, template.IncompleteHeaderB64, StringComparison.Ordinal);

            if(!isNewBlock && !forceUpdate)
                return (false, forceUpdate);

            var job = new PearlBitcoinJob();
            job.Init(template, NextJobId());

            CurrentMiningConfig = template.MiningConfig;

            if(isNewBlock)
            {
                if(via != null)
                    logger.Info(() => $"Detected new Pearl block {template.Height} [{via}]");
                else
                    logger.Info(() => $"Detected new Pearl block {template.Height}");

                BlockchainStats.LastNetworkBlockTime = clock.Now;
                BlockchainStats.BlockHeight = (ulong) template.Height;
                BlockchainStats.NetworkDifficulty = job.BlockDifficulty;
                BlockchainStats.NextNetworkTarget = template.BlockTargetHex;
                BlockchainStats.NextNetworkBits = template.BlockNBitsHex;
            }

            currentJob = job;

            lock(jobLock)
            {
                validJobs[job.JobId] = job;
                jobIdQueue.Enqueue(job.JobId);
                while(validJobs.Count > MaxCachedJobs && jobIdQueue.Count > 0)
                {
                    var oldestKey = jobIdQueue.Dequeue();
                    validJobs.Remove(oldestKey);
                }
            }

            if(isNewBlock)
                messageBus.NotifyChainHeight(poolConfig.Id, (ulong) template.Height, poolConfig.Template);

            return (isNewBlock, forceUpdate);
        }
        catch(OperationCanceledException)
        {
            // ignored
        }
        catch(Exception ex)
        {
            logger.Error(ex, () => $"Error during {nameof(UpdateJob)}");
        }

        return (false, forceUpdate);
    }

    /// <summary>
    /// Forces an immediate template refresh from the pool-service. Used by the
    /// pool's job-refresh loop to ensure miners always get fresh work.
    /// </summary>
    public async Task<object> ForceJobRefreshAsync(CancellationToken ct)
    {
        var result = await UpdateJob(ct, true, "job-refresh");
        return (result.IsNew || result.Force) ? (object) currentJob : null;
    }

    // ----------------------------------------------------------- sidecar I/O

    private async Task<PearlBlockTemplate> FetchPoolTemplateAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(extraPoolConfig.PoolServiceTimeoutMs);
        try
        {
            using var resp = await http.GetAsync(
                $"{poolServiceBaseUrl}/v1/pool-template", HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if(!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                logger.Debug(() => $"pool-template returned {(int) resp.StatusCode}: {body}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            return JsonConvert.DeserializeObject<PearlBlockTemplate>(json);
        }
        catch(OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<TResp> PostJsonAsync<TResp>(string path, object payload, int timeoutMs, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);

        var body = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var resp = await http.PostAsync($"{poolServiceBaseUrl}{path}", content, cts.Token);
        var respBody = await resp.Content.ReadAsStringAsync(cts.Token);
        if(!resp.IsSuccessStatusCode && string.IsNullOrEmpty(respBody))
            throw new HttpRequestException($"{path} returned {(int) resp.StatusCode}");
        return JsonConvert.DeserializeObject<TResp>(respBody);
    }

    // ----------------------------------------------------------- helpers

    /// <summary>
    /// Convert a Bitcoin-style stratum difficulty (in diff-1 units) to a 4-byte
    /// Bitcoin compact "nbits" hex string. The pearl-pool-service uses this nbits
    /// to compute the actual jackpot difficulty bound (with the h*w*k multiplier).
    /// </summary>
    public static string DifficultyToNBitsHex(double difficulty)
    {
        if(difficulty <= 0)
            difficulty = 1;
        // diff1_target = BitcoinConstants.Diff1
        // target = diff1_target / difficulty
        var diff1 = BitcoinConstants.Diff1;
        var targetBig = diff1 / new System.Numerics.BigInteger(Math.Max(1, difficulty));
        if(targetBig <= 0)
            targetBig = 1;
        var target = new Target(targetBig);
        return target.ToCompact().ToString("x8");
    }

    // ----------------------------------------------------------- DTOs

    private class VerifyShareResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("meets_share")] public bool MeetsShare { get; set; }
        [JsonProperty("meets_block")] public bool MeetsBlock { get; set; }
        [JsonProperty("jackpot_hash_hex")] public string JackpotHashHex { get; set; }
        [JsonProperty("elapsed_ms")] public double ElapsedMs { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }

    private class ProveAndSubmitResponse
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("status")] public string Status { get; set; }
        [JsonProperty("height")] public long? Height { get; set; }
        [JsonProperty("elapsed_ms")] public double ElapsedMs { get; set; }
        [JsonProperty("reason")] public string Reason { get; set; }
    }
}
