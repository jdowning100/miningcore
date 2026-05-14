using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Configuration;
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
/// Stratum endpoint for Pearl mining pools.
///
/// Implements the <c>pearl/v1</c> Stratum extension:
///   - <c>mining.configure</c>: capability handshake, miner must declare <c>pearl/v1</c>.
///   - <c>mining.subscribe</c>: returns the usual notify/set_difficulty subscription
///     plus a one-shot <c>pearl.set_mining_params</c> notification carrying the
///     pool-wide mining config.
///   - <c>mining.notify</c>: 7-field job array
///     <c>[job_id, prev_hash_hex, incomplete_header_hex, height, ntime_hex,
///         share_nbits_hex, clean_jobs]</c>
///   - <c>mining.submit</c>: 3-field share
///     <c>[worker_name, job_id, plain_proof_b64]</c>
///     (no extranonce, no nonce — the PlainProof is self-contained).
///
/// Share verification + block submission are delegated to <see cref="PearlBitcoinJobManager"/>,
/// which in turn talks to <c>pearl-pool-service</c> over HTTP.
/// </summary>
[CoinFamily(CoinFamily.Pearl)]
public class PearlPool : PoolBase
{
    public PearlPool(IComponentContext ctx,
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

    protected PearlBitcoinJobManager manager;
    private BitcoinTemplate coin;
    private const string PearlCapability = "pearl/v1";
    private const string PearlSetMiningParamsMethod = "pearl.set_mining_params";
    private const int JobRefreshIntervalSeconds = 3;

    // ---------------------------------------------------------------- subscribe

    protected virtual async Task OnSubscribeAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var context = connection.ContextAs<BitcoinWorkerContext>();
        var requestParams = request.ParamsAs<string[]>();

        // mining.subscribe response: standard 2-tuple of (subscription details, extranonce1 placeholder).
        // Pearl doesn't use extranonce, but Stratum v1 miners typically expect this shape.
        var data = new object[]
        {
            new object[]
            {
                new object[] { BitcoinStratumMethods.SetDifficulty, connection.ConnectionId },
                new object[] { BitcoinStratumMethods.MiningNotify, connection.ConnectionId }
            },
            "",  // extranonce1 (unused in Pearl)
            0    // extranonce2_size (unused in Pearl)
        };

        await connection.RespondAsync(new JsonRpcResponse<object[]>(data, request.Id));

        context.IsSubscribed = true;
        context.UserAgent = requestParams?.FirstOrDefault()?.Trim();

        // Send the pool's mining config so the miner can configure its kernel
        // before the first job arrives. This notification is the entire pearl/v1
        // delta on top of standard Stratum subscribe.
        var miningConfig = manager.CurrentMiningConfig;
        if(miningConfig != null)
        {
            await connection.NotifyAsync(PearlSetMiningParamsMethod, new object[]
            {
                new
                {
                    m = miningConfig.M,
                    n = miningConfig.N,
                    k = miningConfig.K,
                    rank = miningConfig.Rank,
                    rows_pattern = miningConfig.RowsPattern,
                    cols_pattern = miningConfig.ColsPattern,
                    mma_type = miningConfig.MmaType,
                }
            });
        }

        // Initial difficulty + first job
        await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

        var jobParams = GetWorkerJobParams(true, context.Difficulty);
        if(jobParams != null)
        {
            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, jobParams);
            context.LastJobSent = clock.Now;
        }
    }

    // ---------------------------------------------------------------- configure

    protected virtual async Task OnConfigureMiningAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest)
    {
        var request = tsRequest.Value;
        if(request.Id == null)
            throw new StratumException(StratumError.MinusOne, "missing request id");

        var requestParams = request.ParamsAs<JToken[]>();
        var extensions = requestParams?.Length > 0 ? requestParams[0].ToObject<string[]>() : Array.Empty<string>();

        var result = new Dictionary<string, object>();
        if(extensions != null)
        {
            foreach(var extension in extensions)
            {
                if(extension == PearlCapability)
                {
                    result[PearlCapability] = true;
                    result[$"{PearlCapability}.share_format"] = "base64";
                }
            }
        }

        await connection.RespondAsync(new JsonRpcResponse<object>(result, request.Id));
    }

    // ---------------------------------------------------------------- authorize

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

        var split = workerValue?.Split('.');
        var minerName = split?.FirstOrDefault()?.Trim();
        var workerName = split?.Skip(1).FirstOrDefault()?.Trim() ?? string.Empty;

        context.IsAuthorized = await ValidateAddressAsync(minerName, ct);

        if(!context.IsAuthorized)
        {
            logger.Info(() => $"[{connection.ConnectionId}] Unauthorized worker {minerName}");
            await connection.RespondErrorAsync(StratumError.UnauthorizedWorker, "Invalid Pearl address", request.Id);
            return;
        }

        context.Miner = minerName;
        context.Worker = workerName;
        await connection.RespondAsync(true, request.Id);

        logger.Info(() => $"[{connection.ConnectionId}] Authorized Pearl worker {workerValue}");

        // Honor a static difficulty hint from the password (e.g. "x,d=4096").
        var staticDiff = GetStaticDiffFromPassparts(passParts);
        if(staticDiff.HasValue && (context.VarDiff != null && staticDiff.Value >= context.VarDiff.Config.MinDiff ||
                                   context.VarDiff == null && staticDiff.Value > context.Difficulty))
        {
            context.VarDiff = null;
            context.SetDifficulty(staticDiff.Value);
            logger.Info(() => $"[{connection.ConnectionId}] Setting static difficulty of {staticDiff.Value}");

            await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });
            var jobParams = GetWorkerJobParams(true, context.Difficulty);
            if(jobParams != null)
            {
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, jobParams);
                context.LastJobSent = clock.Now;
            }
        }
    }

    // ---------------------------------------------------------------- submit

    protected virtual async Task OnSubmitAsync(StratumConnection connection, Timestamped<JsonRpcRequest> tsRequest, CancellationToken ct)
    {
        var request = tsRequest.Value;
        var context = connection.ContextAs<BitcoinWorkerContext>();

        try
        {
            if(request.Id == null)
                throw new StratumException(StratumError.MinusOne, "missing request id");

            context.LastActivity = clock.Now;

            var requestParams = request.ParamsAs<string[]>();

            var share = await manager.SubmitShareAsync(connection, requestParams, context.Difficulty, ct);

            await connection.RespondAsync(true, request.Id);
            messageBus.SendMessage(share);

            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, true);

            if(context.VarDiff != null)
                await UpdateVarDiffAsync(connection, false, ct);
        }
        catch(StratumException ex)
        {
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);

            var message = ex.Message;
            await connection.RespondErrorAsync(ex.Code, message, request.Id, false);
            logger.Info(() => $"[{connection.ConnectionId}] Share rejected: {message} [{context.UserAgent}]");
            ConsiderBan(connection, context, poolConfig.Banning);
        }
        catch(Exception ex)
        {
            logger.Error(ex, $"[{connection.ConnectionId}] Unexpected error processing share: {ex.GetType().Name}");
            PublishTelemetry(TelemetryCategory.Share, clock.Now - tsRequest.Timestamp.UtcDateTime, false);
            await connection.RespondErrorAsync(StratumError.Other, "internal error", request.Id, false);
        }
    }

    // ---------------------------------------------------------------- broadcast

    private object GetWorkerJobParams(bool cleanJob, double workerDifficulty)
    {
        var job = manager.GetJobForStratum();
        if(job == null)
            return null;

        var shareNBitsHex = PearlBitcoinJobManager.DifficultyToNBitsHex(workerDifficulty);
        return job.GetJobParams(shareNBitsHex, cleanJob);
    }

    protected virtual async Task OnNewJobAsync(object job)
    {
        if(job is not PearlBitcoinJob)
            return;

        logger.Info(() => $"Broadcasting Pearl job {((PearlBitcoinJob) job).JobId}");

        await Guard(() => ForEachMinerAsync(async (connection, ct) =>
        {
            var context = connection.ContextAs<BitcoinWorkerContext>();
            if(!context.IsSubscribed || !context.IsAuthorized)
                return;

            var jobParams = GetWorkerJobParams(true, context.Difficulty);
            if(jobParams == null)
                return;

            if(context.ApplyPendingDifficulty())
                await connection.NotifyAsync(BitcoinStratumMethods.SetDifficulty, new object[] { context.Difficulty });

            await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, jobParams);
            context.LastJobSent = clock.Now;
        }));
    }

    private async Task RunJobRefreshAsync(CancellationToken ct)
    {
        const int CheckIntervalSeconds = 5;
        await Guard(async () =>
        {
            while(!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(CheckIntervalSeconds), ct);

                var now = clock.Now;
                var refreshThreshold = TimeSpan.FromSeconds(JobRefreshIntervalSeconds);

                var refreshed = await manager.ForceJobRefreshAsync(ct);
                if(refreshed == null)
                    continue;

                await Guard(() => ForEachMinerAsync(async (connection, _ct) =>
                {
                    var context = connection.ContextAs<BitcoinWorkerContext>();
                    if(!context.IsSubscribed || !context.IsAuthorized)
                        return;

                    if(now - context.LastJobSent >= refreshThreshold)
                    {
                        var jobParams = GetWorkerJobParams(true, context.Difficulty);
                        if(jobParams == null)
                            return;
                        await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, jobParams);
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

    // ---------------------------------------------------------------- helpers

    protected Task<bool> ValidateAddressAsync(string address, CancellationToken ct)
    {
        // Pearl bech32m taproot addresses. Mainnet uses the "prl1p..." prefix; testnet
        // uses "tprl1p..."; simnet/regtest may use "sprl1..." / "prtb1...". For the MVP
        // we accept any of those HRPs with reasonable length. Deeper verification
        // (checksum, version bits) can land later or come from the node.
        if(string.IsNullOrEmpty(address))
            return Task.FromResult(false);

        var hrpOk = address.StartsWith("prl1",  StringComparison.OrdinalIgnoreCase)  // mainnet
                 || address.StartsWith("tprl1", StringComparison.OrdinalIgnoreCase)  // testnet
                 || address.StartsWith("sprl1", StringComparison.OrdinalIgnoreCase)  // simnet
                 || address.StartsWith("prtb1", StringComparison.OrdinalIgnoreCase); // regtest
        var ok = hrpOk && address.Length >= 60;
        return Task.FromResult(ok);
    }

    // ---------------------------------------------------------------- overrides

    public override void Configure(PoolConfig pc, ClusterConfig cc)
    {
        coin = pc.Template.As<BitcoinTemplate>();
        base.Configure(pc, cc);
    }

    protected override async Task SetupJobManager(CancellationToken ct)
    {
        manager = ctx.Resolve<PearlBitcoinJobManager>();
        manager.Configure(poolConfig, clusterConfig);
        await manager.StartAsync(ct);

        if(poolConfig.EnableInternalStratum == true)
        {
            disposables.Add(manager.Jobs
                .Select(job => Observable.FromAsync(() =>
                    Guard(() => OnNewJobAsync(job),
                        ex => logger.Debug(() => $"{nameof(OnNewJobAsync)}: {ex.Message}"))))
                .Concat()
                .Subscribe(_ => { }, ex => logger.Debug(ex, nameof(OnNewJobAsync))));

            await manager.Jobs.Take(1).ToTask(ct);
            _ = RunJobRefreshAsync(ct);
        }
        else
        {
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
                case BitcoinStratumMethods.MiningConfigure:
                    await OnConfigureMiningAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.Subscribe:
                    await OnSubscribeAsync(connection, tsRequest);
                    break;

                case BitcoinStratumMethods.Authorize:
                    await OnAuthorizeAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SubmitShare:
                    await OnSubmitAsync(connection, tsRequest, ct);
                    break;

                case BitcoinStratumMethods.SuggestDifficulty:
                    await connection.RespondAsync(true, request.Id);
                    break;

                default:
                    logger.Debug(() => $"[{connection.ConnectionId}] Unsupported Pearl Stratum request: {request.Method}");
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
            var jobParams = GetWorkerJobParams(false, context.Difficulty);
            if(jobParams != null)
            {
                await connection.NotifyAsync(BitcoinStratumMethods.MiningNotify, jobParams);
                context.LastJobSent = clock.Now;
            }
        }
    }

    public override double HashrateFromShares(double shares, double interval)
    {
        // Pearl's share target is expanded by h*w*k, while each jackpot
        // candidate costs h*w*k MACs to test. Those factors cancel, so one
        // normalized Stratum difficulty unit represents 2^32 expected MACs.
        return shares * BitcoinConstants.Pow2x32 / interval;
    }

    public override double ShareMultiplier => coin?.ShareMultiplier ?? 1;
}
