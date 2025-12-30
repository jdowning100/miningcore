using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using Autofac;
using Miningcore.Blockchain.Bitcoin.Configuration;
using Miningcore.Blockchain.Bitcoin.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Contracts;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;

namespace Miningcore.Blockchain.Bitcoin;

public class QuaiBitcoinJobManager : JobManagerBase<QuaiBitcoinJob>
{
    public QuaiBitcoinJobManager(
        IComponentContext ctx,
        IMasterClock clock,
        IMessageBus messageBus,
        IExtraNonceProvider extraNonceProvider) :
        base(ctx, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);
        Contract.RequiresNonNull(extraNonceProvider);

        this.clock = clock;
        this.extraNonceProvider = extraNonceProvider;
    }

    protected readonly IMasterClock clock;
    protected RpcClient rpc;
    protected readonly IExtraNonceProvider extraNonceProvider;
    protected const int ExtranonceBytes = 4;
    public int maxActiveJobs { get; protected set; } = 4;
    protected BitcoinPoolConfigExtra extraPoolConfig;
    protected DateTime? lastJobRebroadcast;
    protected TimeSpan jobRebroadcastTimeout;
    protected BitcoinTemplate.BitcoinNetworkParams networkParams;
    protected BitcoinTemplate coin;

    // Quai-specific: track quaiHeight for stale detection
    protected long currentQuaiHeight;

    // Job cache for accepting slightly stale shares
    // Large cache allows miners to submit shares for older jobs while new templates arrive
    protected readonly Dictionary<string, QuaiBitcoinJob> validJobs = new();
    protected readonly Queue<string> jobIdQueue = new();  // Track insertion order for proper FIFO pruning
    protected const int MaxCachedJobs = 100;

    // Stale share threshold: reject shares with quaiHeightDiff >= this value
    // (but still submit as blocks if they meet block target)
    protected const int StaleQuaiHeightThreshold = 2;

    // Algorithm detection (SHA-256 vs Scrypt)
    protected bool isScrypt;
    protected string algorithmName;

    /// <summary>
    /// RPC method for getting block template
    /// </summary>
    protected virtual string BlockTemplateRpcMethod => "quai_getBlockTemplate";

    /// <summary>
    /// RPC method for submitting blocks (depends on algorithm)
    /// </summary>
    protected virtual string SubmitBlockRpcMethod => isScrypt ? "quai_submitScryptBlock" : "quai_submitShaBlock";

    protected virtual object[] GetBlockTemplateParams()
    {
        return new object[]
        {
            new
            {
                rules = new[] { algorithmName },
                coinbase = poolConfig.Address
            }
        };
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

        // Quai requires continuous polling to detect both new blocks and quaiHeight changes
        // Default to 1000ms (1 second) if not configured
        var pollingInterval = poolConfig.BlockRefreshInterval > 0 ? poolConfig.BlockRefreshInterval : 1000;

        triggers.Add(Observable.Timer(TimeSpan.FromMilliseconds(pollingInterval))
            .TakeUntil(pollTimerRestart)
            .Select(_ => (false, JobRefreshBy.Poll, (string) null))
            .Repeat());

        // Periodically force job refresh
        if(poolConfig.JobRebroadcastTimeout > 0)
        {
            triggers.Add(Observable.Timer(jobRebroadcastTimeout)
                .TakeUntil(pollTimerRestart)
                .Select(_ => (true, JobRefreshBy.PollRefresh, (string) null))
                .Repeat());
        }

        Jobs = Observable.Merge(triggers)
            .Select(x => Observable.FromAsync(() => UpdateJob(ct, x.Force, x.Via, x.Data)))
            .Concat()
            .Where(x => x.IsNew || x.Force)
            .Do(x =>
            {
                if(x.IsNew)
                    hasInitialBlockTemplate = true;
            })
            .Select(x => GetJobParamsForStratum(x.IsNew))
            .Publish()
            .RefCount();
    }

    protected async Task<RpcResponse<QuaiBlockTemplate>> GetBlockTemplateAsync(CancellationToken ct)
    {
        var result = await rpc.ExecuteAsync<QuaiBlockTemplate>(logger,
            BlockTemplateRpcMethod, ct, GetBlockTemplateParams());
        return result;
    }

    private QuaiBitcoinJob CreateJob()
    {
        return new QuaiBitcoinJob();
    }

    protected async Task<(bool IsNew, bool Force)> UpdateJob(CancellationToken ct, bool forceUpdate, string via = null, string json = null)
    {
        try
        {
            if(forceUpdate)
                lastJobRebroadcast = clock.Now;

            var response = await GetBlockTemplateAsync(ct);

            // May happen if daemon is currently not connected to peers
            if(response.Error != null)
            {
                logger.Warn(() => $"Unable to update job. Daemon responded with: {response.Error.Message} Code {response.Error.Code}");
                return (false, forceUpdate);
            }

            var blockTemplate = response.Response;
            var job = currentJob;

            // Detect new block by prevhash change or height change
            var isNewBlock = job == null ||
                (blockTemplate != null &&
                    (job.BlockTemplate?.PreviousBlockHash != blockTemplate.PreviousBlockHash ||
                     blockTemplate.Height > job.BlockTemplate?.Height));

            // Detect quaiHeight change (may trigger clean jobs even without new block)
            var previousQuaiHeight = job?.QuaiHeight ?? 0;
            var quaiHeightChanged = job != null && blockTemplate != null &&
                blockTemplate.QuaiHeight != previousQuaiHeight;

            var isNew = isNewBlock || quaiHeightChanged;

            // Notify chain height changes using QuaiHeight (not the merge-mining template height)
            if(isNewBlock || quaiHeightChanged)
                messageBus.NotifyChainHeight(poolConfig.Id, (ulong)blockTemplate.QuaiHeight, poolConfig.Template);

            if(isNew || forceUpdate)
            {
                job = CreateJob();

                job.Init(blockTemplate, NextJobId(),
                    poolConfig, clusterConfig, clock, networkParams,
                    ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue);

                // Update quaiHeight tracking
                currentQuaiHeight = blockTemplate.QuaiHeight;

                if(isNewBlock)
                {
                    if(via != null)
                        logger.Info(() => $"Detected new block {blockTemplate.Height} (quaiHeight={blockTemplate.QuaiHeight}) [{via}]");
                    else
                        logger.Info(() => $"Detected new block {blockTemplate.Height} (quaiHeight={blockTemplate.QuaiHeight})");

                    // Update stats - use QuaiHeight for the actual chain height (not the merge-mining template height)
                    BlockchainStats.LastNetworkBlockTime = clock.Now;
                    BlockchainStats.BlockHeight = (ulong)blockTemplate.QuaiHeight;
                    BlockchainStats.NetworkDifficulty = job.Difficulty;
                    BlockchainStats.NextNetworkTarget = blockTemplate.Target;
                    BlockchainStats.NextNetworkBits = blockTemplate.Bits;
                }
                else if(quaiHeightChanged)
                {
                    logger.Info(() => $"QuaiHeight changed {previousQuaiHeight} -> {blockTemplate.QuaiHeight} at block {blockTemplate.Height}");

                    // Update block height when quaiHeight changes
                    BlockchainStats.BlockHeight = (ulong)blockTemplate.QuaiHeight;
                }
                else
                {
                    if(via != null)
                        logger.Debug(() => $"Template update {blockTemplate?.Height} [{via}]");
                    else
                        logger.Debug(() => $"Template update {blockTemplate?.Height}");
                }

                currentJob = job;

                // Add to job cache for accepting slightly stale shares
                // Must use jobLock for thread-safe access (same lock used in SubmitShareAsync)
                lock(jobLock)
                {
                    validJobs[job.JobId] = job;
                    jobIdQueue.Enqueue(job.JobId);  // Track insertion order
                    logger.Debug(() => $"Added job {job.JobId} to cache (total: {validJobs.Count})");

                    // Prune oldest jobs using FIFO order from queue
                    while(validJobs.Count > MaxCachedJobs && jobIdQueue.Count > 0)
                    {
                        var oldestKey = jobIdQueue.Dequeue();
                        validJobs.Remove(oldestKey);
                    }
                }
            }

            return (isNew, forceUpdate);
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

    protected object GetJobParamsForStratum(bool isNew)
    {
        var job = currentJob;
        return job?.GetJobParams(isNew);
    }

    public override QuaiBitcoinJob GetJobForStratum()
    {
        return currentJob;
    }

    protected record SubmitResult(bool Accepted, string CoinbaseTx, int Status);

    protected async Task<SubmitResult> SubmitBlockAsync(Share share, string blockHex, CancellationToken ct)
    {
        // Quai uses quai_submitShaBlock/quai_submitScryptBlock with 0x prefix
        var blockHexWithPrefix = blockHex.StartsWith("0x") ? blockHex : "0x" + blockHex;

        var submitResult = await rpc.ExecuteAsync<QuaiSubmitBlockResponse>(logger, SubmitBlockRpcMethod, ct, new[] { blockHexWithPrefix });

        if(submitResult.Error != null)
        {
            var submitError = submitResult.Error.Message ?? submitResult.Error.Code.ToString(CultureInfo.InvariantCulture);
            logger.Warn(() => $"Block {share.BlockHeight} submission failed with: {submitError}");
            messageBus.SendMessage(new AdminNotification("Block submission failed",
                $"Pool {poolConfig.Id} failed to submit block {share.BlockHeight}: {submitError}"));
            return new SubmitResult(false, null, -1);
        }

        var response = submitResult.Response;
        var status = response?.StatusValue ?? -1;
        var statusName = status switch
        {
            0 => "Sub (work share only)",
            1 => "Valid",
            2 => "Block",
            _ => $"Unknown({status})"
        };

        logger.Info(() => $"Block {share.BlockHeight} submission response: status={statusName} hash={response?.Hash} number={response?.Number}");

        // Status 0 = Sub (work share only, not a valid block)
        // Status 1 = Valid (valid work)
        // Status 2 = Block (valid block)
        var isAccepted = response?.IsAccepted ?? false;

        if(!isAccepted)
        {
            logger.Warn(() => $"Block {share.BlockHeight} was not accepted by network (status={status})");
        }

        return new SubmitResult(isAccepted, response?.Hash, status);
    }

    protected override async Task EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));

        var syncPendingNotificationShown = false;

        do
        {
            var response = await rpc.ExecuteAsync<QuaiBlockTemplate>(logger,
                BlockTemplateRpcMethod, ct, GetBlockTemplateParams());

            var isSynched = response.Error == null;

            if(isSynched)
            {
                logger.Info(() => "Daemon synched with blockchain");
                break;
            }

            logger.Debug(() => $"Daemon reports error: {response.Error?.Message}");

            if(!syncPendingNotificationShown)
            {
                logger.Info(() => "Daemon is still syncing with network. Manager will be started once synced.");
                syncPendingNotificationShown = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        } while(await timer.WaitForNextTickAsync(ct));
    }

    protected override void ConfigureDaemons()
    {
        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();
        rpc = new RpcClient(poolConfig.Daemons.First(), jsonSerializerSettings, messageBus, poolConfig.Id);
    }

    protected override async Task<bool> AreDaemonsHealthyAsync(CancellationToken ct)
    {
        // Try to get block template as health check
        var response = await rpc.ExecuteAsync<QuaiBlockTemplate>(logger, BlockTemplateRpcMethod, ct, GetBlockTemplateParams());
        return response.Error == null;
    }

    protected override async Task<bool> AreDaemonsConnectedAsync(CancellationToken ct)
    {
        // Try to get block template as connection check
        var response = await rpc.ExecuteAsync<QuaiBlockTemplate>(logger, BlockTemplateRpcMethod, ct, GetBlockTemplateParams());
        return response.Error == null;
    }

    protected override async Task PostStartInitAsync(CancellationToken ct)
    {
        // Ensure daemon is synced before starting
        await EnsureDaemonsSynchedAsync(ct);

        SetupJobUpdates(ct);
    }

    #region API-Surface

    public IObservable<object> Jobs { get; private set; }
    public BlockchainStats BlockchainStats { get; } = new();

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        // Must call base.Configure first to initialize logger
        base.Configure(pc, cc);

        coin = pc.Template.As<BitcoinTemplate>();
        extraPoolConfig = pc.Extra.SafeExtensionDataAs<BitcoinPoolConfigExtra>();

        if(extraPoolConfig?.MaxActiveJobs.HasValue == true)
            maxActiveJobs = extraPoolConfig.MaxActiveJobs.Value;

        // Detect algorithm (SHA-256 vs Scrypt) from coin configuration
        // Check if headerHasher contains "scrypt" or coin name contains "scrypt"
        isScrypt = pc.Coin.ToLower().Contains("scrypt") ||
            (coin.HeaderHasherValue?.GetType()?.Name?.Contains("Scrypt", StringComparison.OrdinalIgnoreCase) ?? false);

        algorithmName = isScrypt ? "scrypt" : "sha";

        logger.Info(() => $"Quai pool configured for {(isScrypt ? "Scrypt" : "SHA-256")} mining (rules: [{algorithmName}])");

        // Determine network params
        var coinLower = pc.Coin.ToLower();
        var isTestnet = coinLower.Contains("testnet");
        networkParams = coin.GetNetwork(isTestnet ? ChainName.Testnet : ChainName.Mainnet);
    }

    public virtual object[] GetSubscriberData(StratumConnection worker)
    {
        Contract.RequiresNonNull(worker);

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Assign unique ExtraNonce1 to worker (miner)
        context.ExtraNonce1 = extraNonceProvider.Next();

        // Get extranonce2 length from current job, or use default
        var job = currentJob;
        var extranonce2Size = job?.Extranonce2Length ?? 8;

        // Setup response data
        var responseData = new object[]
        {
            context.ExtraNonce1,
            extranonce2Size,
        };

        return responseData;
    }

    public virtual async ValueTask<Share> SubmitShareAsync(StratumConnection worker, object submission, CancellationToken ct)
    {
        Contract.RequiresNonNull(worker);
        Contract.RequiresNonNull(submission);

        if(submission is not object[] submitParams)
            throw new StratumException(StratumError.Other, "invalid params");

        var context = worker.ContextAs<BitcoinWorkerContext>();

        // Extract params
        var workerValue = (submitParams[0] as string)?.Trim();
        var jobId = submitParams[1] as string;
        var extraNonce2 = submitParams[2] as string;
        var nTime = submitParams[3] as string;
        var nonce = submitParams[4] as string;
        var versionBits = context.VersionRollingMask.HasValue && submitParams.Length > 5 ? submitParams[5] as string : null;

        if(string.IsNullOrEmpty(workerValue))
            throw new StratumException(StratumError.Other, "missing or invalid workername");

        // Look up job from cache (supports slightly stale jobs)
        QuaiBitcoinJob job;
        long quaiHeightDiff;

        lock(jobLock)
        {
            if(!validJobs.TryGetValue(jobId, out job))
            {
                var cachedJobIds = string.Join(", ", jobIdQueue.Take(5));
                var newestJobs = string.Join(", ", jobIdQueue.Reverse().Take(5));
                logger.Debug(() => $"Job {jobId} not found. Cache has {validJobs.Count} jobs: oldest=[{cachedJobIds}] newest=[{newestJobs}]");
                throw new StratumException(StratumError.JobNotFound, "job not found");
            }

            quaiHeightDiff = currentQuaiHeight - job.QuaiHeight;
        }

        // Validate & process the share (even if stale - might be a block candidate)
        // Returns: PowHash (Scrypt or SHA256d depending on algo), HeaderSha256d (always SHA256d of header)
        var (share, blockHex, powHash, headerSha256d, blockPercent) = job.ProcessShare(worker, extraNonce2, nTime, nonce, versionBits);

        // Enrich share with common data
        share.PoolId = poolConfig.Id;
        share.IpAddress = worker.RemoteEndpoint.Address.ToString();
        share.Miner = context.Miner;
        share.Worker = context.Worker;
        share.UserAgent = context.UserAgent;
        share.Source = poolConfig.Id;  // Pool ID - matches shares.poolid for PPLNS queries
        share.BlockType = algorithmName;  // "sha" or "scrypt" - stored in blocks.type for block candidates
        share.Created = clock.Now;

        // Check staleness based on quaiHeight difference (like alphapool):
        // - Accept: quaiHeightDiff < 2 (current or 1 behind)
        // - Reject: quaiHeightDiff >= 2 (2+ behind)
        var isStale = quaiHeightDiff >= StaleQuaiHeightThreshold;

        if(isStale)
        {
            logger.Info(() => $"Stale share from {context.Miner}: job quaiHeight={job.QuaiHeight}, current={currentQuaiHeight} (diff={quaiHeightDiff})");

            // Even stale shares that meet block target should be submitted (might still be accepted!)
            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Stale {algorithmName.ToUpper()} BLOCK candidate from {context.Miner}! Submitting anyway: height={share.BlockHeight} [{share.BlockHash}]");
                logger.Info(() => $"  PoW hash ({algorithmName}): {powHash}");
                logger.Info(() => $"  Header SHA256d: {headerSha256d}");

                var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

                if(acceptResponse.Accepted)
                {
                    logger.Info(() => $"STALE BLOCK ACCEPTED! height={share.BlockHeight} [{share.BlockHash}] by {context.Miner}");
                    OnBlockFound();
                    share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
                }
                else
                {
                    share.IsBlockCandidate = false;
                }
            }

            // Reject stale share (but it was still processed for potential block submission)
            throw new StratumException(StratumError.Other, "stale share");
        }

        // Log slightly stale but accepted shares (quaiHeightDiff == 1)
        if(quaiHeightDiff > 0)
            logger.Debug(() => $"Slightly stale share accepted: job quaiHeight={job.QuaiHeight}, current={currentQuaiHeight}");

        // If block candidate, submit & check if accepted by network
        if(share.IsBlockCandidate)
        {
            logger.Info(() => $"Submitting {algorithmName.ToUpper()} block {share.BlockHeight} [{share.BlockHash}]");
            logger.Info(() => $"  PoW hash ({algorithmName}): {powHash}");
            logger.Info(() => $"  Header SHA256d: {headerSha256d}");

            var acceptResponse = await SubmitBlockAsync(share, blockHex, ct);

            // Is it still a block candidate?
            share.IsBlockCandidate = acceptResponse.Accepted;

            if(share.IsBlockCandidate)
            {
                logger.Info(() => $"Daemon accepted block {share.BlockHeight} [{share.BlockHash}] submitted by {context.Miner}");

                OnBlockFound();

                // Persist the transaction hash for payment verification
                share.TransactionConfirmationData = acceptResponse.CoinbaseTx;
            }
            else
            {
                // Clear fields that no longer apply
                share.TransactionConfirmationData = null;
            }
        }

        // Record share for hashrate tracking
        context.Stats.RecordShare(share.Difficulty, clock.Now);

        // Log estimated hashrate (after enough shares)
        // For Scrypt: powHash is Scrypt hash, headerSha256d is SHA256d of header
        // For SHA-256: both are the same (SHA256d)
        var estimatedHashrate = context.Stats.GetEstimatedHashrate();
        if(estimatedHashrate > 0)
        {
            logger.Info(() => $"[{context.Miner}] Share accepted: D={Math.Round(share.Difficulty * (coin?.ShareMultiplier ?? 1), 3)} " +
                $"block%={blockPercent:F6}% pow={powHash} " +
                $"(est. {FormatUtil.FormatHashrate(estimatedHashrate)} over {context.Stats.ValidShares} shares)");
        }
        else
        {
            logger.Info(() => $"[{context.Miner}] Share accepted: D={Math.Round(share.Difficulty * (coin?.ShareMultiplier ?? 1), 3)} " +
                $"block%={blockPercent:F6}% pow={powHash}");
        }

        return share;
    }

    public double ShareMultiplier => coin?.ShareMultiplier ?? 1;

    /// <summary>
    /// Forces a template refresh from the node and returns the new job params.
    /// Used by QuaiPool's job refresh loop to ensure miners always get fresh work.
    /// </summary>
    public async Task<object> ForceJobRefreshAsync(CancellationToken ct)
    {
        var result = await UpdateJob(ct, true, "job-refresh", null);

        if(result.IsNew || result.Force)
        {
            return GetJobParamsForStratum(result.IsNew);
        }

        return null;
    }

    #endregion // API-Surface
}
