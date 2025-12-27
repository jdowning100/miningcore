using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.JsonRpc;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Stratum;
using Miningcore.Time;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Bitcoin;

/// <summary>
/// Pool implementation for Quai Network SHA256 mining.
/// Uses QuaiBitcoinJobManager which handles Quai's custom getBlockTemplate format
/// with pre-computed coinbase parts and merkle branches.
/// </summary>
[CoinFamily(CoinFamily.Quai)]
public class QuaiPool : PoolBase
{
    public QuaiPool(IComponentContext ctx,
        JsonSerializerSettings serializerSettings,
        IConnectionFactory cf,
        IStatsRepository statsRepo,
        IMapper mapper,
        IMasterClock clock,
        IMessageBus messageBus,
        RecyclableMemoryStreamManager rmsm,
        NicehashService nicehashService) :
        base(ctx, serializerSettings, cf, statsRepo, mapper, clock, messageBus, rmsm, nicehashService)
    {
    }

    protected object currentJobParams;
    protected QuaiBitcoinJobManager manager;
    private BitcoinTemplate coin;

    /// <summary>
    /// Interval in seconds for forcing job refresh if miner hasn't received an update.
    /// This ensures miners don't work on stale jobs for too long.
    /// </summary>
    private const int JobRefreshIntervalSeconds = 3;

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        var data = new object[]
        {
            new object[]
            {
                new object[] { BitcoinStratumMethods.SetDifficulty, connection.ConnectionId },
                new object[] { BitcoinStratumMethods.MiningNotify, connection.ConnectionId }
            }
        }
        .Concat(manager.GetSubscriberData(connection))
        .ToArray();

        var response = new JsonRpcResponse<object[]>(data, request.Id);

        // Support for ASICBoost
        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        await connection.RespondAsync(response);

        // Setup worker context
        context.IsSubscribed = true;
        context.UserAgent = requestParams.FirstOrDefault()?.Trim();

        // Send initial difficulty and job immediately after subscribe
        // (This is required by most miners/ASICs - they expect job right after subscribe)
        var minerJobParams = GetWorkerJobParams(true);

        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);

        // Track when job was sent for periodic refresh
        context.LastJobSent = clock.Now;
    }

    /// <summary>
    /// Gets the current job params for the worker.
    /// Job caching is handled at the manager level (QuaiBitcoinJobManager.validJobs).
    /// </summary>
    private object GetWorkerJobParams(bool cleanJob)
    {
        var job = manager.GetJobForStratum();
        return job?.GetJobParams(cleanJob) ?? currentJobParams;
    }

    protected virtual async Task OnAuthorizeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var requestParams = request.ParamsAs<string[]>();
        var workerValue = requestParams?.Length > 0 ? requestParams[0] : null;
        var password = requestParams?.Length > 1 ? requestParams[1] : null;
        var passParts = password?.Split(PasswordControlVarsSeparator);

        // Extract worker name
        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        // Validate address
        context.IsAuthorized = await ValidateAddressAsync(minerName, ct);

        if(!context.IsAuthorized)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Unauthorized worker {minerName}");
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Invalid wallet address", request.Id);
            return;
        }

        context.Miner = minerName;
        context.Worker = workerName;

        // Respond
        await connection.RespondAsync(true, request.Id);

        // Log
        logger.Info(() => $"[{connection.ConnectionId}] Authorized worker {workerValue}");

        // Handle difficulty from password (only send set_difficulty if it changed)
        var staticDiff = GetStaticDiffFromPassparts(passParts);
        if(staticDiff.HasValue && (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
        {
            context.VarDiff = null;
            context.SetDifficulty(staticDiff.Value);

            logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

            // Send new difficulty AND a new job to force miner to restart at correct difficulty
            // Without a new job, ASICs continue submitting shares from work started at old difficulty
            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // Send new job with clean=true to ensure miner discards old work
            var minerJobParams = GetWorkerJobParams(true);
            if(minerJobParams != null)
            {
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
                context.LastJobSent = clock.Now;
            }
        }
    }

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            var requestParams = request.ParamsAs<string[]>();

            // Submit
            var share = await manager.SubmitShareAsync(connection, requestParams, ct);

            // Success
            await connection.RespondAsync(true, request.Id);

            // Publish
            messageBus.SendMessage(share);

            // Update stats
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            // Check difficulty adjustment
            if(context.VarDiff != null)
                await UpdateVarDiffAsync(connection, false, ct);
        }
        catch(StratumException ex)
        {
            // Telemetry
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            // Sanitize message - detect corrupted UTF-16 data
            var message = ex.Message;
            if(!string.IsNullOrEmpty(message) && message.Any(c => c > 127 && !char.IsLetterOrDigit(c)))
            {
                logger.Error(() => $"[{connection.ConnectionId}] Corrupted exception message detected! Stack: {ex.StackTrace}");
                message = "internal error - corrupted message";
            }

            // Respond with error
            await connection.RespondErrorAsync(ex.Code, message, request.Id, false);

            // Log
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {message} [{context.UserAgent}]");

            // Check if too many invalid shares
            ConsiderBan(connection, context, poolConfig.Banning);
        }
        catch(Exception ex)
        {
            // Catch any unexpected exceptions
            logger.Error(ex, $"[{connection.ConnectionId}] Unexpected error processing share: {ex.GetType().Name}");

            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);
            await connection.RespondErrorAsync(StratumError.Other, "internal error", request.Id, false);
        }
    }

    protected virtual async Task OnConfigureMiningAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        // Standard stratum mining.configure format:
        // params[0]: string[] of extension names (e.g., ["version-rolling"])
        // params[1]: Dictionary of extension parameters (e.g., {"version-rolling.mask": "1fffe000"})
        var requestParams = request.ParamsAs<JToken[]>();
        var extensions = requestParams[0].ToObject<string[]>();
        var extensionParams = requestParams.Length > 1
            ? requestParams[1].ToObject<Dictionary<string, JToken>>()
            : new Dictionary<string, JToken>();

        var result = new Dictionary<string, object>();

        if(extensions != null)
        {
            foreach(var extension in extensions)
            {
                switch(extension)
                {
                    case BitcoinStratumExtensions.VersionRolling:
                        ConfigureVersionRolling(connection, context, extensionParams, result);
                        break;
                }
            }
        }

        // Nicehash's validator requires "error" property in successful responses
        var response = new JsonRpcResponse<object>(result, request.Id);

        if(context.IsNicehash || poolConfig.EnableAsicBoost == true)
        {
            response.Extra = new Dictionary<string, object>();
            response.Extra["error"] = null;
        }

        await connection.RespondAsync(response);
    }

    private void ConfigureVersionRolling(StratumConnection connection, BitcoinWorkerContext context,
        IReadOnlyDictionary<string, JToken> extensionParams, Dictionary<string, object> result)
    {
        var requestedMask = BitcoinConstants.VersionRollingPoolMask;

        if(extensionParams.TryGetValue(BitcoinStratumExtensions.VersionRollingMask, out var requestedMaskValue))
            requestedMask = uint.Parse(requestedMaskValue.Value<string>(), System.Globalization.NumberStyles.HexNumber);

        // Compute effective mask
        context.VersionRollingMask = BitcoinConstants.VersionRollingPoolMask & requestedMask;

        // Enable version-rolling
        result[BitcoinStratumExtensions.VersionRolling] = true;
        result[BitcoinStratumExtensions.VersionRollingMask] = context.VersionRollingMask.Value.ToStringHex8();

        logger.Info(() => $"[{connection.ConnectionId}] Using version-rolling mask {result[BitcoinStratumExtensions.VersionRollingMask]}");
    }

    protected virtual async Task OnNewJobAsync(object jobParams)
    {
        currentJobParams = jobParams;

        // Extract cleanJob flag from params (last element)
        var cleanJob = jobParams is object[] paramsArray && paramsArray.Length > 0
            ? (bool)paramsArray[^1]
            : false;

        logger.Info(() => $"Broadcasting job {(jobParams is object[] jp ? jp[0] : "?")}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();

            if(!context.IsSubscribed || !context.IsAuthorized)
                return;

            // Get job params for worker
            var minerJobParams = GetWorkerJobParams(cleanJob);

            // Check difficulty adjustment
            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            // Send job and track time
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, minerJobParams);
            context.LastJobSent = clock.Now;
        }));
    }

    /// <summary>
    /// Periodically fetches a fresh template from the node and broadcasts to idle miners.
    /// This ensures miners always get fresh work with new job IDs, preventing duplicate
    /// share rejections after blocks are found.
    /// </summary>
    private async Task RunJobRefreshAsync(CancellationToken ct)
    {
        // Check interval - use Task.Delay to guarantee minimum spacing between iterations
        // even if processing many miners takes longer than the interval
        const int CheckIntervalSeconds = 5;

        await Guard(async () =>
        {
            while(!ct.IsCancellationRequested)
            {
                // Wait at the start to ensure minimum spacing between iterations
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), ct);

                var now = clock.Now;
                var refreshThreshold = TimeSpan.FromSeconds(JobRefreshIntervalSeconds);


                // Fetch fresh template from node (creates new job with new ID)
                var freshJobParams = await manager.ForceJobRefreshAsync(ct);
                if(freshJobParams == null)
                    continue;

                // Update currentJobParams for consistency
                currentJobParams = freshJobParams;

                // Extract job ID for logging
                var jobId = freshJobParams is object[] jp ? jp[0]?.ToString() : "?";

                // Broadcast to idle miners
                await Guard(() => ForEachMinerAsync(async (connection, _ct) =>
                {
                    var context = connection.ContextAs<BitcoinWorkerContext>();

                    if(!context.IsSubscribed || !context.IsAuthorized)
                        return;

                    // Check if job refresh is needed for this miner
                    var timeSinceLastJob = now - context.LastJobSent;
                    if(timeSinceLastJob >= refreshThreshold)
                    {
                        logger.Info(() => $"[{connection.ConnectionId}] Job refresh {jobId} after {timeSinceLastJob.TotalSeconds:F1}s idle");
                        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, freshJobParams);
                        context.LastJobSent = now;
                    }
                }, ct));
            }
        }, ex =>
        {
            if(ex is not OperationCanceledException)
                logger.Error(ex);
        });
    }

    protected async Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
    {
        // Basic validation - Quai addresses should be valid Ethereum-style addresses
        if(string.IsNullOrEmpty(address))
            return false;

        // Check if it's a valid hex address (0x prefix + 40 hex chars)
        if(address.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = address.Substring(2);
            return hex.Length == 40 && hex.All(c => Uri.IsHexDigit(c));
        }

        return false;
    }

    #region Overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();

        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<QuaiBitcoinJobManager>(
            new TypedParameter(typeof(IExtraNonceProvider), new BitcoinExtraNonceProvider(poolConfig.Id, clusterConfig.InstanceId)));

        manager.Configure(poolConfig, clusterConfig);

        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(() => OnNewJobAsync(job),
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex =>
                {
                    logger.Debug(ex, nameof(OnNewJobAsync));
                }));

            // Start with initial blocktemplate
            await manager.Jobs.Take(1).ToTask(ct);

            // Start periodic job refresh to prevent miners working on stale jobs
            _ = RunJobRefreshAsync(ct);
        }

        else
        {
            // Keep polling once per second
            disposables.Add(manager.Jobs.Subscribe());
        }
    }

    protected override async Task InitStatsAsync(CancellationToken ct)
    {
        await base.InitStatsAsync(ct);

        blockchainStats = manager.BlockchainStats;
    }

    protected override WorkerContextBase CreateWorkerContext()
    {
        return new BitcoinWorkerContext();
    }

    protected override async Task OnRequestAsync(StratumConnection connection,
        Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;

        try
        {
            switch(request.Method)
            {
                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.MiningConfigure:
                    await OnConfigureMiningAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    // Acknowledge but don't change difficulty
                    await connection.RespondAsync(true, request.Id);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported RPC request: {JsonConvert.SerializeObject(request, serializerSettings)}");
                    await connection.RespondErrorAsync(StratumError.Other, $"Unsupported request {request.Method}", request.Id);
                    break;
            }
        }
        catch(StratumException ex)
        {
            await connection.RespondErrorAsync(ex.Code, ex.Message, request.Id, false);
        }
    }

    protected override async Task OnVarDiffUpdateAsync(StratumConnection connection, double newDiff, CancellationToken ct)
    {
        await base.OnVarDiffUpdateAsync(connection, newDiff, ct);

        var context = connection.ContextAs<BitcoinWorkerContext>();

        if(context.ApplyPendingDifficulty())
        {
            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, currentJobParams);
            context.LastJobSent = clock.Now;
        }
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        // shares = sum of internal difficulties from database (already normalized: stratumDiff / shareMultiplier)
        // To get expected hashes: internalDiff * 2^32 (same formula as GetEstimatedHashrate)
        // This works correctly for both SHA-256 (shareMultiplier=1) and Scrypt (shareMultiplier=65536)
        var result = shares * BitcoinConstants.Pow2x32 / interval;
        return result;
    }

    public override double ShareMultiplier => coin?.ShareMultiplier ?? 1;

    #endregion // Overrides
}
