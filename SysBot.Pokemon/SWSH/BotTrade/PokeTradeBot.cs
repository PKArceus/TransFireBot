using PKHeX.Core;
using PKHeX.Core.Searching;
using SysBot.Base;
using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using static SysBot.Base.SwitchButton;
using static SysBot.Pokemon.PokeDataOffsets;
using System.Collections.Generic;
using System.IO;

namespace SysBot.Pokemon
{
    public class PokeTradeBot : PokeRoutineExecutor8, ICountBot
    {
        public static ISeedSearchHandler<PK8> SeedChecker = new NoSeedSearchHandler<PK8>();
        private readonly PokeTradeHub<PK8> Hub;
        private readonly TradeSettings TradeSettings;
        private readonly TradeAbuseSettings AbuseSettings;
        public ICountSettings Counts => TradeSettings;

        private static readonly TrackedUserLog PreviousUsers = new();
        private static readonly TrackedUserLog PreviousUsersDistribution = new();
        private static readonly TrackedUserLog EncounteredUsers = new();

        /// <summary>
        /// Folder to dump received trade data to.
        /// </summary>
        /// <remarks>If null, will skip dumping.</remarks>
        private readonly IDumper DumpSetting;

        /// <summary>
        /// Synchronized start for multiple bots.
        /// </summary>
        public bool ShouldWaitAtBarrier { get; private set; }

        /// <summary>
        /// Tracks failed synchronized starts to attempt to re-sync.
        /// </summary>
        public int FailedBarrier { get; private set; }

        private readonly string TradeF;

        public PokeTradeBot(PokeTradeHub<PK8> hub, PokeBotState cfg) : base(cfg)
        {
            Hub = hub;
            TradeSettings = hub.Config.Trade;
            AbuseSettings = hub.Config.TradeAbuse;
            DumpSetting = hub.Config.Folder;
            TradeF = hub.Config.Folder.TradeFolder;
            lastOffered = new byte[8];

        }

        // Cached offsets that stay the same per session.
        private ulong OverworldOffset;

        // Track the last Pokémon we were offered since it persists between trades.
        private byte[] lastOffered;

        public override async Task MainLoop(CancellationToken token)
        {
            try
            {
                await InitializeHardware(Hub.Config.Trade, token).ConfigureAwait(false);

                Log("识别主机控制台的训练器数据.");
                var sav = await IdentifyTrainer(token).ConfigureAwait(false);
                RecentTrainerCache.SetRecentTrainer(sav);
                await InitializeSessionOffsets(token).ConfigureAwait(false);

                Log($"正在启动 {nameof(PokeTradeBot)} 循环.");
                await InnerLoop(sav, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log(e.Message);
            }

            Log($"正在结束 {nameof(PokeTradeBot)} 循环.");
            await HardStop().ConfigureAwait(false);
        }

        public override async Task HardStop()
        {
            UpdateBarrier(false);
            await CleanExit(TradeSettings, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task InnerLoop(SAV8SWSH sav, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                Config.IterateNextRoutine();
                var task = Config.CurrentRoutineType switch
                {
                    PokeRoutineType.Idle => DoNothing(token),
                    PokeRoutineType.SurpriseTrade => DoSurpriseTrades(sav, token),
                    _ => DoTrades(sav, token),
                };
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    if (e.StackTrace != null)
                        Connection.LogError(e.StackTrace);
                    var attempts = Hub.Config.Timings.ReconnectAttempts;
                    var delay = Hub.Config.Timings.ExtraReconnectDelay;
                    var protocol = Config.Connection.Protocol;
                    if (!await TryReconnect(attempts, delay, protocol, token).ConfigureAwait(false))
                        return;
                }
            }
        }

        private async Task DoNothing(CancellationToken token)
        {
            int waitCounter = 0;
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.Idle)
            {
                if (waitCounter == 0)
                    Log("未分配任务。等待新任务分配.");
                waitCounter++;
                if (waitCounter % 10 == 0 && Hub.Config.AntiIdle)
                    await Click(B, 1_000, token).ConfigureAwait(false);
                else
                    await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task DoTrades(SAV8SWSH sav, CancellationToken token)
        {
            var type = Config.CurrentRoutineType;
            int waitCounter = 0;
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == type)
            {
                var (detail, priority) = GetTradeData(type);
                if (detail is null)
                {
                    await WaitForQueueStep(waitCounter++, token).ConfigureAwait(false);
                    continue;
                }
                waitCounter = 0;

                detail.IsProcessing = true;
                string tradetype = $" ({detail.Type})";
                Log($"开始下一个 {type}{tradetype} 机器人交易，获取数据...");
                Hub.Config.Stream.StartTrade(this, detail, Hub);
                Hub.Queues.StartTrade(this, detail);

                await PerformTrade(sav, detail, type, priority, token).ConfigureAwait(false);
            }
        }

        private async Task WaitForQueueStep(int waitCounter, CancellationToken token)
        {
            if (waitCounter == 0)
            {
                // Updates the assets.
                Hub.Config.Stream.IdleAssets(this);
                Log("没有要检查的，等待新用户...");
            }

            const int interval = 10;
            if (waitCounter % interval == interval-1 && Hub.Config.AntiIdle)
                await Click(B, 1_000, token).ConfigureAwait(false);
            else
                await Task.Delay(1_000, token).ConfigureAwait(false);
        }

        protected virtual (PokeTradeDetail<PK8>? detail, uint priority) GetTradeData(PokeRoutineType type)
        {
            if (Hub.Queues.TryDequeue(type, out var detail, out var priority))
                return (detail, priority);
            if (Hub.Queues.TryDequeueLedy(out detail))
                return (detail, PokeTradePriorities.TierFree);
            return (null, PokeTradePriorities.TierFree);
        }

        private async Task PerformTrade(SAV8SWSH sav, PokeTradeDetail<PK8> detail, PokeRoutineType type, uint priority, CancellationToken token)
        {
            PokeTradeResult result;
            try
            {
                result = await PerformLinkCodeTrade(sav, detail, token).ConfigureAwait(false);
                if (result == PokeTradeResult.Success)
                    return;
            }
            catch (SocketException socket)
            {
                Log(socket.Message);
                result = PokeTradeResult.ExceptionConnection;
                HandleAbortedTrade(detail, type, priority, result);
                throw; // let this interrupt the trade loop. re-entering the trade loop will recheck the connection.
            }
            catch (Exception e)
            {
                Log(e.Message);
                result = PokeTradeResult.ExceptionInternal;
            }

            HandleAbortedTrade(detail, type, priority, result);
        }

        private void HandleAbortedTrade(PokeTradeDetail<PK8> detail, PokeRoutineType type, uint priority, PokeTradeResult result)
        {
            detail.IsProcessing = false;
            if (result.ShouldAttemptRetry() && detail.Type != PokeTradeType.Random && !detail.IsRetry)
            {
                detail.IsRetry = true;
                Hub.Queues.Enqueue(type, detail, Math.Min(priority, PokeTradePriorities.Tier2));
                detail.SendNotification(this, "Oops! Something happened. I'll requeue you for another attempt.");
            }
            else
            {
                detail.SendNotification(this, $"Oops! Something happened. Canceling the trade: {result}.");
                detail.TradeCanceled(this, result);
            }
        }

        private async Task DoSurpriseTrades(SAV8SWSH sav, CancellationToken token)
        {
            await SetCurrentBox(0, token).ConfigureAwait(false);
            while (!token.IsCancellationRequested && Config.NextRoutineType == PokeRoutineType.SurpriseTrade)
            {
                var pkm = Hub.Ledy.Pool.GetRandomSurprise();
                await EnsureConnectedToYComm(OverworldOffset, Hub.Config, token).ConfigureAwait(false);
                var _ = await PerformSurpriseTrade(sav, pkm, token).ConfigureAwait(false);
            }
        }

        private async Task<PokeTradeResult> PerformLinkCodeTrade(SAV8SWSH sav, PokeTradeDetail<PK8> poke, CancellationToken token)
        {
            // Update Barrier Settings
            UpdateBarrier(poke.IsSynchronized);
            poke.TradeInitialize(this);
            await EnsureConnectedToYComm(OverworldOffset, Hub.Config, token).ConfigureAwait(false);
            Hub.Config.Stream.EndEnterCode(this);

            if (await CheckIfSoftBanned(token).ConfigureAwait(false))
                await UnSoftBan(token).ConfigureAwait(false);

            var toSend = poke.TradeData;
            LogUtil.LogInfo($"尝试写入盒子,宝可梦种类id:{toSend.Species}", nameof(PokeTradeBot));
            if (toSend.Species != 0)
            {
                await SetBoxPokemon(toSend, 0, 0, token, sav).ConfigureAwait(false);
                LogUtil.LogInfo($"已经写入盒子,宝可梦种类id:{toSend.Species}", nameof(PokeTradeBot));
            }
           
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                await ExitTrade(true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            while (await CheckIfSearchingForLinkTradePartner(token).ConfigureAwait(false))
            {
                Log("仍在搜索，重置机器人位置.");
                await ResetTradePosition(token).ConfigureAwait(false);
            }

            Log("打开Y-Comm菜单.");
            await Click(Y, 2_000, token).ConfigureAwait(false);

            Log("选择链接交易.");
            await Click(A, 1_500, token).ConfigureAwait(false);

            Log("选择链接交易代码.");
            await Click(DDOWN, 500, token).ConfigureAwait(false);

            for (int i = 0; i < 2; i++)
                await Click(A, 1_500, token).ConfigureAwait(false);

            // All other languages require an extra A press at this menu.
            if (GameLang != LanguageID.English && GameLang != LanguageID.Spanish)
                await Click(A, 1_500, token).ConfigureAwait(false);

            // Loading Screen
            if (poke.Type != PokeTradeType.Random)
                Hub.Config.Stream.StartEnterCode(this);
            await Task.Delay(Hub.Config.Timings.ExtraTimeOpenCodeEntry, token).ConfigureAwait(false);

            var code = poke.Code;
            Log($"输入链接交易代码: {code:0000 0000}...");
            await EnterLinkCode(code, Hub.Config, token).ConfigureAwait(false);

            // Wait for Barrier to trigger all bots simultaneously.
            WaitAtBarrierIfApplicable(token);
            await Click(PLUS, 1_000, token).ConfigureAwait(false);

            Hub.Config.Stream.EndEnterCode(this);

            // Confirming and return to overworld.
            var delay_count = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                if (delay_count++ >= 5)
                {
                    // Too many attempts, recover out of the trade.
                    await ExitTrade(true, token).ConfigureAwait(false);
                    return PokeTradeResult.RecoverPostLinkCode;
                }

                for (int i = 0; i < 5; i++)
                    await Click(A, 0_800, token).ConfigureAwait(false);
            }

            poke.TradeSearching(this);
            await Task.Delay(0_500, token).ConfigureAwait(false);

            // Wait for a Trainer...
            var partnerFound = await WaitForTradePartnerOffer(token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;
            if (!partnerFound)
            {
                await ResetTradePosition(token).ConfigureAwait(false);
                return PokeTradeResult.NoTrainerFound;
            }

            // Select Pokemon
            // pkm already injected to b1s1
            await Task.Delay(5_500 + Hub.Config.Timings.ExtraTimeOpenBox, token).ConfigureAwait(false); // necessary delay to get to the box properly

            var trainerName = await GetTradePartnerName(TradeMethod.LinkTrade, token).ConfigureAwait(false);
            var trainerTID = await GetTradePartnerTID7(TradeMethod.LinkTrade, token).ConfigureAwait(false);
            var trainerNID = await GetTradePartnerNID(token).ConfigureAwait(false);
            RecordUtil<PokeTradeBot>.Record($"正在启动\t{trainerNID:X16}\t{trainerName}\t{poke.Trainer.TrainerName}\t{poke.Trainer.ID}\t{poke.ID}\t{toSend.EncryptionConstant:X8}");
            Log($"找到连接交换对象: {trainerName}-TID:{trainerTID} (任天堂网络ID: {trainerNID})");

            var partnerCheck = await CheckPartnerReputation(poke, trainerNID, trainerName, token).ConfigureAwait(false);
            if (partnerCheck != PokeTradeResult.Success)
            {
                await ExitSeedCheckTrade(token).ConfigureAwait(false);
                return partnerCheck;
            }

            if (!await IsInBox(token).ConfigureAwait(false))
            {
                await ExitTrade(true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverOpenBox;
            }
            
            if (Hub.Config.Legality.UseTradePartnerInfo)
            {
                await SetPkmWithSwappedIDDetails(toSend, trainerName, sav, token);
            }

            // //Confirm Box 1 Slot 1
            //if (poke.Type == PokeTradeType.Specific)
            //{
            //    for (int i = 0; i < 5; i++)
            //        await Click(A, 0_500, token).ConfigureAwait(false);
            //}
            

            var tradeOffered = await ReadUntilChanged(LinkTradePartnerPokemonOffset, lastOffered, 10_000, 0_500, false, true, token).ConfigureAwait(false);
            if (!tradeOffered)
                return PokeTradeResult.TrainerTooSlow;
            poke.SendNotification(this, $"Found Link Trade partner: {trainerName}. Waiting for a Pokémon...");

            if (poke.Type == PokeTradeType.Dump)
                return await ProcessDumpTradeAsync(poke, token).ConfigureAwait(false);

            // Wait for User Input...
            var offered = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            //var oldEC = await Connection.ReadBytesAsync(LinkTradePartnerPokemonOffset, 4, token).ConfigureAwait(false);
            //if (offered is null)
            //{
            //    await ExitSeedCheckTrade(token).ConfigureAwait(false);
            //    return PokeTradeResult.TrainerTooSlow;
            //}

            if (poke.Type == PokeTradeType.Seed)
            {
                // Immediately exit, we aren't trading anything.
                return await EndSeedCheckTradeAsync(poke, offered, token).ConfigureAwait(false);
            }

            List<PK8> ls = new List<PK8>();
            if (poke.Type == PokeTradeType.MutiTrade || poke.DeletFile)
            {
               
                string directory = Path.Combine(TradeF, poke.Path);
                string[] fileEntries = Directory.GetFiles(directory);
                Array.Sort(fileEntries);
                Log($"读取到的文件数量:{fileEntries.Length}");
                foreach (string fileName in fileEntries)
                {
                    var data = File.ReadAllBytes(fileName);
                    // LogUtil.LogInfo($"读取到文件:{fileName}",nameof(PokeTradeBotSV));
                    var pkt = EntityFormat.GetFromBytes(data);
                    if (pkt != null)
                    {
                        pkt.RefreshChecksum();
                        if (EntityConverter.ConvertToType(pkt, typeof(PK8), out _) is PK8 pk2)
                            ls.Add(pk2);
                    }
                }
                if (Directory.Exists(directory) && poke.DeletFile)
                {
                    foreach (var item in Directory.GetFiles(directory))
                    {
                        File.Delete(item);
                    }
                    Directory.Delete(directory);
                }

            }
            else
            {
                ls.Add(poke.TradeData);
            }
         //   PK8 offered = toSend;
            int counting = 0;
            foreach (var send in ls)
            {
                counting++;
                toSend = send;

                
                //先写一次
                await SetBoxPokemon(toSend, 0, 0, token, sav).ConfigureAwait(false);

                if (Hub.Config.Legality.UseTradePartnerInfo)
                {
                    await SetPkmWithSwappedIDDetails(toSend, trainerName, sav, token);
                }


                // Wait for User Input...
                offered = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 25_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("ReadUntilPresent方法返回结果为null."); ;
                var oldEC = await Connection.ReadBytesAsync(LinkTradePartnerPokemonOffset, 4, token).ConfigureAwait(false);
                if (offered is null)
                {
                    await ExitSeedCheckTrade(token).ConfigureAwait(false);
                    return PokeTradeResult.TrainerTooSlow;
                }
                //if (poke.Type == PokeTradeType.Seed)
                //{
                //    // Immediately exit, we aren't trading anything.
                //    return await EndSeedCheckTradeAsync(poke, offered, token).ConfigureAwait(false);
                //}
                PokeTradeResult update;
                var trainer = new PartnerDataHolder(trainerNID, trainerName, trainerTID);
                (toSend, update) = await GetEntityToSend(sav, poke, offered, oldEC, toSend, trainer, token).ConfigureAwait(false);
                if (update != PokeTradeResult.Success)
                {
                    await ExitTrade(false, token).ConfigureAwait(false);
                    return update;
                }

                var tradeResult = await ConfirmAndStartTrading(poke, token).ConfigureAwait(false);
                if (tradeResult != PokeTradeResult.Success)
                {
                    await ExitTrade(false, token).ConfigureAwait(false);
                    return tradeResult;
                }
                if (ls.Count > 1)
                {
                    poke.SendNotification(this, $"批量:第{counting}只宝可梦{ShowdownTranslator<PK9>.GameStringsZh.Species[toSend.Species]}，交换完成");
                    LogUtil.LogInfo($"批量:等待交换第{counting}个宝可梦{ShowdownTranslator<PK9>.GameStringsZh.Species[toSend.Species]}", nameof(PokeTradeBot));
                }
                if (token.IsCancellationRequested)
                {
                    await ExitTrade(false, token).ConfigureAwait(false);
                    return PokeTradeResult.RoutineCancel;
                }
            }      
                // Trade was Successful!
                var received = await ReadBoxPokemon(0, 0, token).ConfigureAwait(false);
            // Pokémon in b1s1 is same as the one they were supposed to receive (was never sent).
            if (SearchUtil.HashByDetails(received) == SearchUtil.HashByDetails(toSend) && received.Checksum == toSend.Checksum)
            {
                Log("用户没有完成交易.");
                RecordUtil<PokeTradeBot>.Record($"取消\t{trainerNID:X16}\t{trainerName}\t{poke.Trainer.TrainerName}\\t{poke.ID}\t{toSend.EncryptionConstant:X8}\t{offered.EncryptionConstant:X8}");
                await ExitTrade(false, token).ConfigureAwait(false);
                return PokeTradeResult.TrainerTooSlow;
            }

            // As long as we got rid of our inject in b1s1, assume the trade went through.
            Log("用户完成交易.");
            poke.TradeFinished(this, received);

            RecordUtil<PokeTradeBot>.Record($"完成\t{trainerNID:X16}\t{toSend.EncryptionConstant:X8}\t{received.EncryptionConstant:X8}");

            // Only log if we completed the trade.
            UpdateCountsAndExport(poke, received, toSend);
            lastOffered = await SwitchConnection.ReadBytesAbsoluteAsync(LinkTradePartnerPokemonOffset, 8, token).ConfigureAwait(false);
            await ExitTrade(false, token).ConfigureAwait(false);
            return PokeTradeResult.Success;
        }

        private async Task<bool> SetPkmWithSwappedIDDetails(PK8 toSend, string trainerName, SAV8SWSH sav, CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(LinkTradePartnerNameOffset - 0x8, 8, token).ConfigureAwait(false);
            var tidsid = BitConverter.ToUInt32(data, 0);
            var cln = (PK8)toSend.Clone();
            cln.OT_Gender = data[6];
            cln.TrainerTID7 = (uint)(tidsid % 1_000_000);
            cln.TrainerSID7 = (uint)(tidsid / 1_000_000);
            cln.Language = data[5];
            cln.OT_Name = trainerName;
            cln.ClearNickname();

            if (toSend.IsShiny)
                cln.SetShiny();

            cln.RefreshChecksum();

            var tradeswsh = new LegalityAnalysis(cln);
            if (tradeswsh.Valid)
            {
                Log($"自ID后合法，使用用户信息");
                await SetBoxPokemon(cln, 0, 0, token, sav).ConfigureAwait(false);
            }
            else
            {
                Log($"自ID后不合法，不修改宝可梦信息");
            }

            return tradeswsh.Valid;
        }

        private async Task<PokeTradeResult> CheckPartnerReputation(PokeTradeDetail<PK8> poke, ulong TrainerNID, string TrainerName, CancellationToken token)
        {
            bool quit = false;
            var user = poke.Trainer;
            var isDistribution = poke.Type == PokeTradeType.Random;
            var useridmsg = isDistribution ? "" : $" ({user.ID})";
            var list = isDistribution ? PreviousUsersDistribution : PreviousUsers;

            var cooldown = list.TryGetPrevious(TrainerNID);
            if (cooldown != null)
            {
                var delta = DateTime.Now - cooldown.Time;
                Log($"上次连接 {user.TrainerName} {delta.TotalMinutes:F1} 分钟前 (OT: {TrainerName}).");

                var cd = AbuseSettings.TradeCooldown;
                if (cd != 0 && TimeSpan.FromMinutes(cd) > delta)
                {
                    poke.Notifier.SendNotification(this, poke, "你无视管理员设置的交换CD. 现已经通知管理员.");
                    var msg = $"发现 {user.TrainerName}{useridmsg}无视 {cd} 分钟的交换CD.上一次遇到是在 {delta.TotalMinutes:F1} 分钟前.";
                    if (AbuseSettings.EchoNintendoOnlineIDCooldown)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.CooldownAbuseEchoMention))
                        msg = $"{AbuseSettings.CooldownAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                    quit = true;
                }
            }

            if (!isDistribution)
            {
                var previousEncounter = EncounteredUsers.TryRegister(poke.Trainer.ID, TrainerName, poke.Trainer.ID);
                if (previousEncounter != null && previousEncounter.Name != TrainerName)
                {
                    if (AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                    {
                        if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                        {
                            await BlockUser(token).ConfigureAwait(false);
                            if (AbuseSettings.BanIDWhenBlockingUser)
                            {
                                AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "给多个游戏存档发送游戏数据") });
                                Log($"已经将{TrainerNID}加入黑名单.");
                            }
                        }
                        quit = true;
                    }

                    var msg = $"发现 {user.TrainerName}{useridmsg} 使用多个游戏存档交换.  上一个角色OT: {previousEncounter.Name}, 当前角色OT: {TrainerName}";
                    if (AbuseSettings.EchoNintendoOnlineIDMultiRecipients)
                        msg += $"\nID: {TrainerNID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiRecipientEchoMention))
                        msg = $"{AbuseSettings.MultiRecipientEchoMention} {msg}";
                    EchoUtil.Echo(msg);
                }
            }

            if (quit)
                return PokeTradeResult.SuspiciousActivity;

            // Try registering the partner in our list of recently seen.
            // Get back the details of their previous interaction.
            var previous = isDistribution
                ? list.TryRegister(TrainerNID, TrainerName)
                : list.TryRegister(TrainerNID, TrainerName, poke.Trainer.ID);
            if (previous != null && previous.NetworkID == TrainerNID && previous.RemoteID != user.ID && !isDistribution)
            {
                var delta = DateTime.Now - previous.Time;
                if (delta < TimeSpan.FromMinutes(AbuseSettings.TradeAbuseExpiration) && AbuseSettings.TradeAbuseAction != TradeAbuseAction.Ignore)
                {
                    if (AbuseSettings.TradeAbuseAction == TradeAbuseAction.BlockAndQuit)
                    {
                        await BlockUser(token).ConfigureAwait(false);
                        if (AbuseSettings.BanIDWhenBlockingUser)
                        {
                            AbuseSettings.BannedIDs.AddIfNew(new[] { GetReference(TrainerName, TrainerNID, "in-game block for multiple accounts") });
                            Log($"Added {TrainerNID} to the BannedIDs list.");
                        }
                    }
                    quit = true;
                }

                var msg = $"发现 {user.TrainerName}{useridmsg}使用多个账户.\n{delta.TotalMinutes:F1}分钟前识别到{previous.Name} ({previous.RemoteID})OT: {TrainerName}.";
                if (AbuseSettings.EchoNintendoOnlineIDMulti)
                    msg += $"\nID: {TrainerNID}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.MultiAbuseEchoMention))
                    msg = $"{AbuseSettings.MultiAbuseEchoMention} {msg}";
                EchoUtil.Echo(msg);
            }

            if (quit)
                return PokeTradeResult.SuspiciousActivity;

            var entry = AbuseSettings.BannedIDs.List.Find(z => z.ID == TrainerNID);
            if (entry != null)
            {
                if (AbuseSettings.BlockDetectedBannedUser)
                    await BlockUser(token).ConfigureAwait(false);

                var msg = $"{user.TrainerName}{useridmsg}是一个黑名单的用户，并且在游戏中使用OT: {TrainerName}.";
                if (!string.IsNullOrWhiteSpace(entry.Comment))
                    msg += $"\n用户因以下原因被禁: {entry.Comment}";
                if (!string.IsNullOrWhiteSpace(AbuseSettings.BannedIDMatchEchoMention))
                    msg = $"{AbuseSettings.BannedIDMatchEchoMention} {msg}";
                EchoUtil.Echo(msg);
                return PokeTradeResult.SuspiciousActivity;
            }

            return PokeTradeResult.Success;
        }

        private static RemoteControlAccess GetReference(string name, ulong id, string comment) => new()
        {
            ID = id,
            Name = name,
            Comment = $"Added automatically on {DateTime.Now:yyyy.MM.dd-hh:mm:ss} ({comment})",
        };

        protected virtual async Task<bool> WaitForTradePartnerOffer(CancellationToken token)
        {
            Log("等待训练家...");
            return await WaitForPokemonChanged(LinkTradePartnerPokemonOffset, Hub.Config.Trade.TradeWaitTime * 1_000, 0_200, token).ConfigureAwait(false);
        }

        private void UpdateCountsAndExport(PokeTradeDetail<PK8> poke, PK8 received, PK8 toSend)
        {
            var counts = TradeSettings;
            if (poke.Type == PokeTradeType.Random)
                counts.AddCompletedDistribution();
            else if (poke.Type == PokeTradeType.Clone)
                counts.AddCompletedClones();
            else
                counts.AddCompletedTrade();

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
            {
                var subfolder = poke.Type.ToString().ToLower();
                DumpPokemon(DumpSetting.DumpFolder, subfolder, received); // received by bot
                if (poke.Type is PokeTradeType.Specific or PokeTradeType.Clone)
                    DumpPokemon(DumpSetting.DumpFolder, "traded", toSend); // sent to partner
            }
        }

        private async Task<PokeTradeResult> ConfirmAndStartTrading(PokeTradeDetail<PK8> detail, CancellationToken token)
        {
            // We'll keep watching B1S1 for a change to indicate a trade started -> should try quitting at that point.
            var oldEC = await Connection.ReadBytesAsync(BoxStartOffset, 8, token).ConfigureAwait(false);

            await Click(A, 3_000, token).ConfigureAwait(false);
            for (int i = 0; i < Hub.Config.Trade.MaxTradeConfirmTime; i++)
            {
                // If we are in a Trade Evolution/PokeDex Entry and the Trade Partner quits, we land on the Overworld
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    return PokeTradeResult.TrainerLeft;
                if (await IsUserBeingShifty(detail, token).ConfigureAwait(false))
                    return PokeTradeResult.SuspiciousActivity;
                await Click(A, 1_000, token).ConfigureAwait(false);

                // EC is detectable at the start of the animation.
                var newEC = await Connection.ReadBytesAsync(BoxStartOffset, 8, token).ConfigureAwait(false);
                if (!newEC.SequenceEqual(oldEC))
                {
                    await Task.Delay(25_000, token).ConfigureAwait(false);
                    return PokeTradeResult.Success;
                }
            }

            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                return PokeTradeResult.TrainerLeft;

            return PokeTradeResult.Success;
        }

        protected virtual async Task<(PK8 toSend, PokeTradeResult check)> GetEntityToSend(SAV8SWSH sav, PokeTradeDetail<PK8> poke, PK8 offered, byte[] oldEC, PK8 toSend, PartnerDataHolder partnerID, CancellationToken token)
        {
            return poke.Type switch
            {
                PokeTradeType.Random => await HandleRandomLedy(sav, poke, offered, toSend, partnerID, token).ConfigureAwait(false),
                PokeTradeType.Clone => await HandleClone(sav, poke, offered, oldEC, token).ConfigureAwait(false),
                _ => (toSend, PokeTradeResult.Success),
            };
        }

        private async Task<(PK8 toSend, PokeTradeResult check)> HandleClone(SAV8SWSH sav, PokeTradeDetail<PK8> poke, PK8 offered, byte[] oldEC, CancellationToken token)
        {
      
            var la = new LegalityAnalysis(offered);
            if (!la.Valid)
            {
                Log($"Clone request (from {poke.Trainer.TrainerName}) has detected an invalid Pokémon: {GameInfo.GetStrings(1).Species[offered.Species]}.");
                if (DumpSetting.Dump)
                    DumpPokemon(DumpSetting.DumpFolder, "hacked", offered);

                var report = la.Report();
                Log(report);
                poke.SendNotification(this, "This Pokémon is not legal per PKHeX's legality checks. I am forbidden from cloning this. Exiting trade.");
                poke.SendNotification(this, report);

                return (offered, PokeTradeResult.IllegalTrade);
            }

            var clone = (PK8)offered.Clone();
            if (Hub.Config.Legality.ResetHOMETracker)
                clone.Tracker = 0;

            poke.SendNotification(this, $"***Cloned your {GameInfo.GetStrings(1).Species[clone.Species]}!***\nNow press B to cancel your offer and trade me a Pokémon you don't want.");
            Log($"克隆了 {(Species)clone.Species}. 等待用户更改他们的神奇宝贝...");

            // Separate this out from WaitForPokemonChanged since we compare to old EC from original read.
            var partnerFound = await ReadUntilChanged(LinkTradePartnerPokemonOffset, oldEC, 15_000, 0_200, false, token).ConfigureAwait(false);

            if (!partnerFound)
            {
                poke.SendNotification(this, "***HEY CHANGE IT NOW OR I AM LEAVING!!!***");
                // They get one more chance.
                partnerFound = await ReadUntilChanged(LinkTradePartnerPokemonOffset, oldEC, 15_000, 0_200, false, token).ConfigureAwait(false);
            }

            var pk2 = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 1_000, BoxFormatSlotSize, token).ConfigureAwait(false);
            if (!partnerFound || pk2 == null || SearchUtil.HashByDetails(pk2) == SearchUtil.HashByDetails(offered))
            {
                Log("Trade partner did not change their Pokémon.");
                return (offered, PokeTradeResult.TrainerTooSlow);
            }

            await Click(A, 0_800, token).ConfigureAwait(false);
            await SetBoxPokemon(clone, 0, 0, token, sav).ConfigureAwait(false);

            for (int i = 0; i < 5; i++)
                await Click(A, 0_500, token).ConfigureAwait(false);

            return (clone, PokeTradeResult.Success);
        }

        private async Task<(PK8 toSend, PokeTradeResult check)> HandleRandomLedy(SAV8SWSH sav, PokeTradeDetail<PK8> poke, PK8 offered, PK8 toSend, PartnerDataHolder partner, CancellationToken token)
        {
            // Allow the trade partner to do a Ledy swap.
            var config = Hub.Config.Distribution;
            var trade = Hub.Ledy.GetLedyTrade(offered, partner.TrainerOnlineID, config.LedySpecies);
            if (trade != null)
            {
                if (trade.Type == LedyResponseType.AbuseDetected)
                {
                    var msg = $"Found {partner.TrainerName} has been detected for abusing Ledy trades.";
                    if (AbuseSettings.EchoNintendoOnlineIDLedy)
                        msg += $"\nID: {partner.TrainerOnlineID}";
                    if (!string.IsNullOrWhiteSpace(AbuseSettings.LedyAbuseEchoMention))
                        msg = $"{AbuseSettings.LedyAbuseEchoMention} {msg}";
                    EchoUtil.Echo(msg);

                    return (toSend, PokeTradeResult.SuspiciousActivity);
                }

                toSend = trade.Receive;
                poke.TradeData = toSend;

                poke.SendNotification(this, "Injecting the requested Pokémon.");
                await Click(A, 0_800, token).ConfigureAwait(false);
                await SetBoxPokemon(toSend, 0, 0, token, sav).ConfigureAwait(false);
                await Task.Delay(2_500, token).ConfigureAwait(false);
            }
            else if (config.LedyQuitIfNoMatch)
            {
                return (toSend, PokeTradeResult.TrainerRequestBad);
            }

            for (int i = 0; i < 5; i++)
            {
                if (await IsUserBeingShifty(poke, token).ConfigureAwait(false))
                    return (toSend, PokeTradeResult.SuspiciousActivity);
                await Click(A, 0_500, token).ConfigureAwait(false);
            }

            return (toSend, PokeTradeResult.Success);
        }

        // For pointer offsets that don't change per session are accessed frequently, so set these each time we start.
        private async Task InitializeSessionOffsets(CancellationToken token)
        {
            Log("缓存会话偏移量...");
            OverworldOffset = await SwitchConnection.PointerAll(Offsets.OverworldPointer, token).ConfigureAwait(false);
        }

        protected virtual async Task<bool> IsUserBeingShifty(PokeTradeDetail<PK8> detail, CancellationToken token)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            return false;
        }

        private async Task RestartGameSWSH(CancellationToken token)
        {
            await ReOpenGame(Hub.Config, token).ConfigureAwait(false);
            await InitializeSessionOffsets(token).ConfigureAwait(false);
        }

        private async Task<PokeTradeResult> ProcessDumpTradeAsync(PokeTradeDetail<PK8> detail, CancellationToken token)
        {
            int ctr = 0;
            var time = TimeSpan.FromSeconds(Hub.Config.Trade.MaxDumpTradeTime);
            var start = DateTime.Now;
            var pkprev = new PK8();
            var bctr = 0;
            while (ctr < Hub.Config.Trade.MaxDumpsPerTrade && DateTime.Now - start < time)
            {
                if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                    break;
                if (bctr++ % 3 == 0)
                    await Click(B, 0_100, token).ConfigureAwait(false);

                var pk = await ReadUntilPresent(LinkTradePartnerPokemonOffset, 3_000, 0_500, BoxFormatSlotSize, token).ConfigureAwait(false);
                if (pk == null || pk.Species < 1 || !pk.ChecksumValid || SearchUtil.HashByDetails(pk) == SearchUtil.HashByDetails(pkprev))
                    continue;

                // Save the new Pokémon for comparison next round.
                pkprev = pk;

                // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
                if (DumpSetting.Dump)
                {
                    var subfolder = detail.Type.ToString().ToLower();
                    DumpPokemon(DumpSetting.DumpFolder, subfolder, pk); // received
                }

                var la = new LegalityAnalysis(pk);
                var verbose = $"```{la.Report(true)}```";
                Log($"Shown Pokémon is: {(la.Valid ? "Valid" : "Invalid")}.");

                ctr++;
                var msg = Hub.Config.Trade.DumpTradeLegalityCheck ? verbose : $"File {ctr}";
                // Extra information for shiny eggs, because of people dumping to skip hatching.
                var eggstring = pk.IsEgg ? "Egg " : string.Empty;
                msg += pk.IsShiny ? $"\n***This Pokémon {eggstring}is shiny!***" : string.Empty;
                detail.SendNotification(this, pk, msg);
            }

            Log($"Ended Dump loop after processing {ctr} Pokémon.");
            await ExitSeedCheckTrade(token).ConfigureAwait(false);
            if (ctr == 0)
                return PokeTradeResult.TrainerTooSlow;

            TradeSettings.AddCompletedDumps();
            detail.Notifier.SendNotification(this, detail, $"Dumped {ctr} Pokémon.");
            detail.Notifier.TradeFinished(this, detail, detail.TradeData); // blank pk8
            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> PerformSurpriseTrade(SAV8SWSH sav, PK8 pkm, CancellationToken token)
        {
            // General Bot Strategy:
            // 1. Inject to b1s1
            // 2. Send out Trade
            // 3. Clear received PKM to skip the trade animation
            // 4. Repeat

            // Inject to b1s1
            if (await CheckIfSoftBanned(token).ConfigureAwait(false))
                await UnSoftBan(token).ConfigureAwait(false);

            Log("Starting next Surprise Trade. Getting data...");
            await SetBoxPokemon(pkm, 0, 0, token, sav).ConfigureAwait(false);

            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                await ExitTrade(true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverStart;
            }

            if (await CheckIfSearchingForSurprisePartner(token).ConfigureAwait(false))
            {
                Log("Still searching, resetting bot position.");
                await ResetTradePosition(token).ConfigureAwait(false);
            }

            Log("Opening Y-Comm menu.");
            await Click(Y, 1_500, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            Log("Selecting Surprise Trade.");
            await Click(DDOWN, 0_500, token).ConfigureAwait(false);
            await Click(A, 2_000, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            await Task.Delay(0_750, token).ConfigureAwait(false);

            if (!await IsInBox(token).ConfigureAwait(false))
            {
                await ExitTrade(true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverPostLinkCode;
            }

            Log($"Selecting Pokémon: {pkm.FileName}");
            // Box 1 Slot 1; no movement required.
            await Click(A, 0_700, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            Log("Confirming...");
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await Click(A, 0_800, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            // Let Surprise Trade be sent out before checking if we're back to the Overworld.
            await Task.Delay(3_000, token).ConfigureAwait(false);

            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                await ExitTrade(true, token).ConfigureAwait(false);
                return PokeTradeResult.RecoverReturnOverworld;
            }

            // Wait 30 Seconds for Trainer...
            Log("Waiting for Surprise Trade partner...");

            // Wait for an offer...
            var oldEC = await Connection.ReadBytesAsync(SurpriseTradeSearchOffset, 4, token).ConfigureAwait(false);
            var partnerFound = await ReadUntilChanged(SurpriseTradeSearchOffset, oldEC, Hub.Config.Trade.TradeWaitTime * 1_000, 0_200, false, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            if (!partnerFound)
            {
                await ResetTradePosition(token).ConfigureAwait(false);
                return PokeTradeResult.NoTrainerFound;
            }

            // Let the game flush the results and de-register from the online surprise trade queue.
            await Task.Delay(7_000, token).ConfigureAwait(false);

            var TrainerName = await GetTradePartnerName(TradeMethod.SurpriseTrade, token).ConfigureAwait(false);
            var TrainerTID = await GetTradePartnerTID7(TradeMethod.SurpriseTrade, token).ConfigureAwait(false);
            var SurprisePoke = await ReadSurpriseTradePokemon(token).ConfigureAwait(false);

            Log($"Found Surprise Trade partner: {TrainerName}-{TrainerTID}, Pokémon: {(Species)SurprisePoke.Species}");

            // Clear out the received trade data; we want to skip the trade animation.
            // The box slot locks have been removed prior to searching.

            await Connection.WriteBytesAsync(BitConverter.GetBytes(SurpriseTradeSearch_Empty), SurpriseTradeSearchOffset, token).ConfigureAwait(false);
            await Connection.WriteBytesAsync(PokeTradeBotUtil.EMPTY_SLOT, SurpriseTradePartnerPokemonOffset, token).ConfigureAwait(false);

            // Let the game recognize our modifications before finishing this loop.
            await Task.Delay(5_000, token).ConfigureAwait(false);

            // Clear the Surprise Trade slot locks! We'll skip the trade animation and reuse the slot on later loops.
            // Write 8 bytes of FF to set both Int32's to -1. Regular locks are [Box32][Slot32]

            await Connection.WriteBytesAsync(BitConverter.GetBytes(ulong.MaxValue), SurpriseTradeLockBox, token).ConfigureAwait(false);

            if (token.IsCancellationRequested)
                return PokeTradeResult.RoutineCancel;

            if (await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                Log("Trade complete!");
            else
                await ExitTrade(true, token).ConfigureAwait(false);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "surprise", SurprisePoke);
            TradeSettings.AddCompletedSurprise();

            return PokeTradeResult.Success;
        }

        private async Task<PokeTradeResult> EndSeedCheckTradeAsync(PokeTradeDetail<PK8> detail, PK8 pk, CancellationToken token)
        {
            await ExitSeedCheckTrade(token).ConfigureAwait(false);

            detail.TradeFinished(this, pk);

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, "seed", pk);

            // Send results from separate thread; the bot doesn't need to wait for things to be calculated.
#pragma warning disable 4014
            Task.Run(() =>
            {
                try
                {
                    ReplyWithSeedCheckResults(detail, pk);
                }
                catch (Exception ex)
                {
                    detail.SendNotification(this, $"Unable to calculate seeds: {ex.Message}\r\n{ex.StackTrace}");
                }
            }, token);
#pragma warning restore 4014

            TradeSettings.AddCompletedSeedCheck();

            return PokeTradeResult.Success;
        }

        private void ReplyWithSeedCheckResults(PokeTradeDetail<PK8> detail, PK8 result)
        {
            detail.SendNotification(this, "Calculating your seed(s)...");

            if (result.IsShiny)
            {
                Log("The Pokémon is already shiny!"); // Do not bother checking for next shiny frame
                detail.SendNotification(this, "This Pokémon is already shiny! Raid seed calculation was not done.");

                if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                    DumpPokemon(DumpSetting.DumpFolder, "seed", result);

                detail.TradeFinished(this, result);
                return;
            }

            SeedChecker.CalculateAndNotify(result, detail, Hub.Config.SeedCheckSWSH, this);
            Log("Seed calculation completed.");
        }

        private void WaitAtBarrierIfApplicable(CancellationToken token)
        {
            if (!ShouldWaitAtBarrier)
                return;
            var opt = Hub.Config.Distribution.SynchronizeBots;
            if (opt == BotSyncOption.NoSync)
                return;

            var timeoutAfter = Hub.Config.Distribution.SynchronizeTimeout;
            if (FailedBarrier == 1) // failed last iteration
                timeoutAfter *= 2; // try to re-sync in the event things are too slow.

            var result = Hub.BotSync.Barrier.SignalAndWait(TimeSpan.FromSeconds(timeoutAfter), token);

            if (result)
            {
                FailedBarrier = 0;
                return;
            }

            FailedBarrier++;
            Log($"Barrier sync timed out after {timeoutAfter} seconds. Continuing.");
        }

        /// <summary>
        /// Checks if the barrier needs to get updated to consider this bot.
        /// If it should be considered, it adds it to the barrier if it is not already added.
        /// If it should not be considered, it removes it from the barrier if not already removed.
        /// </summary>
        private void UpdateBarrier(bool shouldWait)
        {
            if (ShouldWaitAtBarrier == shouldWait)
                return; // no change required

            ShouldWaitAtBarrier = shouldWait;
            if (shouldWait)
            {
                Hub.BotSync.Barrier.AddParticipant();
                Log($"Joined the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
            else
            {
                Hub.BotSync.Barrier.RemoveParticipant();
                Log($"Left the Barrier. Count: {Hub.BotSync.Barrier.ParticipantCount}");
            }
        }

        private async Task<bool> WaitForPokemonChanged(uint offset, int waitms, int waitInterval, CancellationToken token)
        {
            // check EC and checksum; some pkm may have same EC if shown sequentially
            var oldEC = await Connection.ReadBytesAsync(offset, 8, token).ConfigureAwait(false);
            return await ReadUntilChanged(offset, oldEC, waitms, waitInterval, false, token).ConfigureAwait(false);
        }

        private async Task ExitTrade(bool unexpected, CancellationToken token)
        {
            if (unexpected)
                Log("Unexpected behavior, recovering position.");

            int attempts = 0;
            int softBanAttempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                var screenID = await GetCurrentScreen(token).ConfigureAwait(false);
                if (screenID == CurrentScreen_Softban)
                {
                    softBanAttempts++;
                    if (softBanAttempts > 10)
                        await RestartGameSWSH(token).ConfigureAwait(false);
                }

                attempts++;
                if (attempts >= 15)
                    break;

                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }
        }

        private async Task ExitSeedCheckTrade(CancellationToken token)
        {
            // Seed Check Bot doesn't show anything, so it can skip the first B press.
            int attempts = 0;
            while (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
            {
                attempts++;
                if (attempts >= 15)
                    break;

                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }

            await Task.Delay(3_000, token).ConfigureAwait(false);
        }

        private async Task ResetTradePosition(CancellationToken token)
        {
            Log("Resetting bot position.");

            // Shouldn't ever be used while not on overworld.
            if (!await IsOnOverworld(OverworldOffset, token).ConfigureAwait(false))
                await ExitTrade(true, token).ConfigureAwait(false);

            // Ensure we're searching before we try to reset a search.
            if (!await CheckIfSearchingForLinkTradePartner(token).ConfigureAwait(false))
                return;

            await Click(Y, 2_000, token).ConfigureAwait(false);
            for (int i = 0; i < 5; i++)
                await Click(A, 1_500, token).ConfigureAwait(false);
            // Extra A press for Japanese.
            if (GameLang == LanguageID.Japanese)
                await Click(A, 1_500, token).ConfigureAwait(false);
            await Click(B, 1_500, token).ConfigureAwait(false);
            await Click(B, 1_500, token).ConfigureAwait(false);
        }

        // Blocks a user from the box during in-game trades.
        protected async Task BlockUser(CancellationToken token)
        {
            Log("Blocking user in-game...");
            await PressAndHold(RSTICK, 0_750, 0, token).ConfigureAwait(false);
            await Click(DUP, 0_300, token).ConfigureAwait(false);
            await Click(A, 1_300, token).ConfigureAwait(false);
            await Click(A, 1_300, token).ConfigureAwait(false);
            await Click(DUP, 0_300, token).ConfigureAwait(false);
            await Click(A, 1_100, token).ConfigureAwait(false);
            await Click(A, 1_100, token).ConfigureAwait(false);
        }

        private async Task<bool> CheckIfSearchingForLinkTradePartner(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(LinkTradeSearchingOffset, 1, token).ConfigureAwait(false);
            return data[0] == 1; // changes to 0 when found
        }

        private async Task<bool> CheckIfSearchingForSurprisePartner(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(SurpriseTradeSearchOffset, 8, token).ConfigureAwait(false);
            return BitConverter.ToUInt32(data, 0) == SurpriseTradeSearch_Searching;
        }

        private async Task<string> GetTradePartnerName(TradeMethod tradeMethod, CancellationToken token)
        {
            var ofs = GetTrainerNameOffset(tradeMethod);
            var data = await Connection.ReadBytesAsync(ofs, 26, token).ConfigureAwait(false);
            return StringConverter8.GetString(data);
        }

        private async Task<string> GetTradePartnerTID7(TradeMethod tradeMethod, CancellationToken token)
        {
            var ofs = GetTrainerTIDSIDOffset(tradeMethod);
            var data = await Connection.ReadBytesAsync(ofs, 8, token).ConfigureAwait(false);

            var tidsid = BitConverter.ToUInt32(data, 0);
            var tid7 = $"{tidsid % 1_000_000:000000}";
            return tid7;
        }

        public async Task<ulong> GetTradePartnerNID(CancellationToken token)
        {
            var data = await Connection.ReadBytesAsync(LinkTradePartnerNIDOffset, 8, token).ConfigureAwait(false);
            return BitConverter.ToUInt64(data, 0);
        }
    }
}
