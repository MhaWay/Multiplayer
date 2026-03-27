using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using LiteNetLib;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    public partial class BootstrapConfiguratorWindow
    {
        private void DrawGenerateMap(Rect entry, Rect inRect)
        {
            Text.Font = GameFont.Small;
            var statusHeight = Text.CalcHeight(statusText ?? string.Empty, entry.width);
            Widgets.Label(entry.Height(statusHeight), statusText ?? string.Empty);
            entry = entry.Down(statusHeight + 10);

            if (!AwaitingBootstrapMapInit && !saveReady && !isUploadingSave && !isReconnecting)
            {
                DrawFactionOwnershipNotice(entry.Height(100f));
                entry = entry.Down(110);
            }

            if (!string.IsNullOrEmpty(saveUploadStatus))
            {
                var saveStatusHeight = Text.CalcHeight(saveUploadStatus, entry.width);
                Widgets.Label(entry.Height(saveStatusHeight), saveUploadStatus);
                entry = entry.Down(saveStatusHeight + 4);
            }

            if (autoAdvanceArmed || isUploadingSave)
            {
                var barRect = entry.Height(18f);
                Widgets.FillableBar(barRect, isUploadingSave ? saveUploadProgress : 0.1f);
                entry = entry.Down(24);
            }

            if (ShouldShowGenerateMapButton() && Widgets.ButtonText(new Rect((inRect.width - 200f) / 2f, inRect.height - 45f, 200f, 40f), "Generate map"))
            {
                saveUploadAutoStarted = false;
                hideWindowDuringMapGen = true;
                StartVanillaNewColonyFlow();
            }

            if (ShouldAutoReconnectForSaveUpload())
            {
                saveUploadAutoStarted = true;
                ReconnectAndUploadSave();
            }
        }

        private void DrawFactionOwnershipNotice(Rect noticeRect)
        {
            GUI.color = new Color(1f, 0.85f, 0.5f);
            Widgets.DrawBoxSolid(noticeRect, new Color(0.3f, 0.25f, 0.1f, 0.5f));
            GUI.color = Color.white;

            var noticeTextRect = noticeRect.ContractedBy(8f);
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(1f, 0.9f, 0.6f);
            Widgets.Label(noticeTextRect,
                "IMPORTANT: The user who generates this map will own the main faction (colony).\n" +
                "When setting up the server, make sure this user's username is listed as the host.\n" +
                "Other players connecting to the server will be assigned as spectators or secondary factions.");
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private bool ShouldShowGenerateMapButton()
        {
            return !(autoAdvanceArmed || AwaitingBootstrapMapInit || saveReady || isUploadingSave || isReconnecting);
        }

        private bool ShouldAutoReconnectForSaveUpload()
        {
            return saveReady && !isUploadingSave && !saveUploadAutoStarted && Multiplayer.Client == null && Current.ProgramState != ProgramState.Playing;
        }

        private void StartVanillaNewColonyFlow()
        {
            if (Multiplayer.session != null)
            {
                Multiplayer.session.Stop();
                Multiplayer.session = null;
            }

            try
            {
                Current.Game ??= new Game();
                Current.Game.InitData ??= new GameInitData { startedFromEntry = true };

                if (Current.Game.components.All(c => c is not BootstrapCoordinator))
                    Current.Game.components.Add(new BootstrapCoordinator(Current.Game));

                var scenarioPage = new Page_SelectScenario();
                Find.WindowStack.Add(PageUtility.StitchedPages(new System.Collections.Generic.List<Page> { scenarioPage }));

                saveReady = false;
                savedReplayPath = null;
                autoAdvanceArmed = true;
                AwaitingBootstrapMapInit = true;
                saveUploadStatus = "Generating map...";
            }
            catch (Exception e)
            {
                Messages.Message($"Failed to start New Colony flow: {e.GetType().Name}: {e.Message}", MessageTypeDefOf.RejectInput, false);
            }
        }

        private void TryArmAwaitingBootstrapMapInit()
        {
            if (AwaitingBootstrapMapInit)
                return;

            if (Multiplayer.Client != null || bootstrapSaveQueued || saveReady || isUploadingSave || isReconnecting || saveUploadAutoStarted)
                return;

            try
            {
                if (LongEventHandler.AnyEventNowOrWaiting)
                    return;
            }
            catch
            {
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;

            if (Find.Maps == null || Find.Maps.Count == 0)
                return;

            AwaitingBootstrapMapInit = true;
            saveUploadStatus = "Entered map. Waiting for initialization to complete...";
            autoAdvanceArmed = false;

            if (Find.WindowStack != null && Find.WindowStack.WindowOfType<BootstrapConfiguratorWindow>() == null)
                Find.WindowStack.Add(this);
        }

        internal void TryArmAwaitingBootstrapMapInit_FromRootPlay()
        {
            TryArmAwaitingBootstrapMapInit();
        }

        internal void TryArmAwaitingBootstrapMapInit_FromRootPlayUpdate()
        {
            TryArmAwaitingBootstrapMapInit();
            TickPostMapEnterSaveDelayAndMaybeSave();
        }

        public void OnBootstrapMapInitialized()
        {
            if (!AwaitingBootstrapMapInit)
                return;

            hideWindowDuringMapGen = false;
            AwaitingBootstrapMapInit = false;
            postMapEnterSaveDelayRemaining = PostMapEnterSaveDelaySeconds;
            awaitingControllablePawns = true;
            awaitingControllablePawnsElapsed = 0f;
            bootstrapSaveQueued = false;
            saveUploadStatus = "Map initialized. Waiting before saving...";
        }

        private void TickPostMapEnterSaveDelayAndMaybeSave()
        {
            if (bootstrapSaveQueued || saveReady || isUploadingSave || isReconnecting)
                return;

            if (postMapEnterSaveDelayRemaining <= 0f && !awaitingControllablePawns)
                return;

            postMapEnterSaveDelayRemaining -= Time.deltaTime;
            if (postMapEnterSaveDelayRemaining > 0f)
                return;

            if (!WaitForControllableColonists())
                return;

            postMapEnterSaveDelayRemaining = 0f;
            bootstrapSaveQueued = true;
            saveUploadStatus = "Map initialized. Starting hosted MP session...";

            LongEventHandler.QueueLongEvent(StartHostedBootstrapSaveCreation, "Starting host", false, null);
        }

        private bool WaitForControllableColonists()
        {
            if (!awaitingControllablePawns)
                return true;

            awaitingControllablePawnsElapsed += Time.deltaTime;

            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
            {
                var anyColonist = false;
                try
                {
                    anyColonist = Find.CurrentMap.mapPawns?.FreeColonistsSpawned != null
                        && Find.CurrentMap.mapPawns.FreeColonistsSpawned.Count > 0;
                }
                catch (Exception ex)
                {
                    Log.Error($"Exception checking for controllable colonists: {ex.GetType().Name}: {ex.Message}");
                }

                if (anyColonist)
                {
                    awaitingControllablePawns = false;
                    try { Find.TickManager.CurTimeSpeed = TimeSpeed.Paused; } catch { }
                }
            }

            if (!awaitingControllablePawns)
                return true;

            if (awaitingControllablePawnsElapsed > AwaitControllablePawnsTimeoutSeconds)
            {
                awaitingControllablePawns = false;
                Log.Warning("Timed out waiting for controllable pawns during bootstrap; saving anyway");
                return true;
            }

            saveUploadStatus = "Waiting for controllable colonists to spawn...";
            return false;
        }

        private void StartHostedBootstrapSaveCreation()
        {
            try
            {
                var hostSettings = new ServerSettings
                {
                    gameName = settings.gameName,
                    maxPlayers = 2,
                    direct = false,
                    lan = false,
                    steam = false,
                };

                if (!HostWindow.HostProgrammatically(hostSettings))
                {
                    OnMainThread.Enqueue(() =>
                    {
                        saveUploadStatus = "Failed to host MP session.";
                        Log.Error("HostProgrammatically failed during bootstrap save creation");
                        bootstrapSaveQueued = false;
                    });
                    return;
                }

                OnMainThread.Enqueue(() =>
                {
                    saveUploadStatus = "Hosted. Saving replay...";
                    LongEventHandler.QueueLongEvent(CreateBootstrapReplaySave, "Saving", false, null);
                });
            }
            catch (Exception e)
            {
                OnMainThread.Enqueue(() =>
                {
                    saveUploadStatus = $"Host failed: {e.GetType().Name}: {e.Message}";
                    Log.Error($"Bootstrap host exception: {e}");
                    bootstrapSaveQueued = false;
                });
            }
        }

        private void CreateBootstrapReplaySave()
        {
            try
            {
                Autosaving.SaveGameToFile_Overwrite(BootstrapSaveName, currentReplay: false);
                var path = Path.Combine(Multiplayer.ReplaysDir, $"{BootstrapSaveName}.zip");
                OnMainThread.Enqueue(() => FinalizeBootstrapSave(path));
            }
            catch (Exception e)
            {
                OnMainThread.Enqueue(() =>
                {
                    saveUploadStatus = $"Save failed: {e.GetType().Name}: {e.Message}";
                    Log.Error($"Bootstrap save failed: {e}");
                    bootstrapSaveQueued = false;
                });
            }
        }

        private void FinalizeBootstrapSave(string path)
        {
            savedReplayPath = path;
            saveReady = File.Exists(savedReplayPath);
            lastSavedReplayPath = savedReplayPath;
            lastSaveReady = saveReady;

            if (!saveReady)
            {
                saveUploadStatus = $"Save finished but file not found: {path}";
                Log.Error($"Bootstrap save finished but file was missing: {savedReplayPath}");
                bootstrapSaveQueued = false;
                return;
            }

            saveUploadAutoStarted = true;
            saveUploadStatus = "Save created. Returning to menu...";
            LongEventHandler.QueueLongEvent(ReturnToMenuAndReconnect, "Returning to menu", false, null);
        }

        private void ReturnToMenuAndReconnect()
        {
            GenScene.GoToMainMenu();
            OnMainThread.Enqueue(() =>
            {
                saveUploadStatus = "Reconnecting to upload save...";
                ReconnectAndUploadSave();
            });
        }

        private void ReconnectAndUploadSave()
        {
            saveUploadStatus = "Reconnecting to server...";

            try
            {
                Multiplayer.StopMultiplayer();

                Multiplayer.session = new MultiplayerSession
                {
                    address = serverAddress,
                    port = serverPort
                };

                var netClient = new NetManager(new MpClientNetListener())
                {
                    EnableStatistics = true,
                    IPv6Enabled = MpUtil.SupportsIPv6() ? IPv6Mode.SeparateSocket : IPv6Mode.Disabled
                };
                netClient.Start();
                netClient.ReconnectDelay = 300;
                netClient.MaxConnectAttempts = 8;

                Multiplayer.session.netClient = netClient;
                netClient.Connect(serverAddress, serverPort, string.Empty);

                isReconnecting = true;
                reconnectCheckTimer = 0;
            }
            catch (Exception e)
            {
                saveUploadStatus = $"Reconnection failed: {e.GetType().Name}: {e.Message}";
                isUploadingSave = false;
            }
        }

        private void CheckReconnectionState()
        {
            reconnectCheckTimer++;

            if (Multiplayer.Client?.State == ConnectionStateEnum.ClientBootstrap)
            {
                saveUploadStatus = "Reconnected. Starting upload...";
                isReconnecting = false;
                reconnectCheckTimer = 0;
                StartUploadSaveZip();
            }
            else if (Multiplayer.Client?.State == ConnectionStateEnum.Disconnected || (Multiplayer.Client == null && reconnectCheckTimer > 300))
            {
                saveUploadStatus = "Reconnection failed. Cannot upload save.zip.";
                isReconnecting = false;
                reconnectCheckTimer = 0;
                isUploadingSave = false;
            }
            else if (reconnectCheckTimer > 600)
            {
                saveUploadStatus = "Reconnection timeout. Cannot upload save.zip.";
                isReconnecting = false;
                reconnectCheckTimer = 0;
                isUploadingSave = false;
            }
        }

        private void StartUploadSaveZip()
        {
            if (string.IsNullOrWhiteSpace(savedReplayPath) || !File.Exists(savedReplayPath))
            {
                saveUploadStatus = "Can't upload: autosave file not found.";
                return;
            }

            isUploadingSave = true;
            saveUploadProgress = 0f;
            saveUploadStatus = "Uploading save.zip...";

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(savedReplayPath);
            }
            catch (Exception e)
            {
                isUploadingSave = false;
                saveUploadStatus = $"Failed to read autosave: {e.GetType().Name}: {e.Message}";
                return;
            }

            var targetConn = Multiplayer.Client;
            if (targetConn == null)
            {
                isUploadingSave = false;
                saveUploadStatus = "No active connection. Cannot upload.";
                return;
            }

            new System.Threading.Thread(() =>
            {
                try
                {
                    targetConn.Send(new ClientBootstrapSaveStartPacket("save.zip", bytes.Length));

                    const int chunk = 256 * 1024;
                    var sent = 0;
                    while (sent < bytes.Length)
                    {
                        var len = Math.Min(chunk, bytes.Length - sent);
                        var part = new byte[len];
                        Buffer.BlockCopy(bytes, sent, part, 0, len);
                        targetConn.SendFragmented(new ClientBootstrapSaveDataPacket(part).Serialize());
                        sent += len;
                        var progress = bytes.Length == 0 ? 1f : (float)sent / bytes.Length;
                        OnMainThread.Enqueue(() => saveUploadProgress = Mathf.Clamp01(progress));
                    }

                    byte[] sha256Hash;
                    using (var hasher = SHA256.Create())
                        sha256Hash = hasher.ComputeHash(bytes);

                    targetConn.Send(new ClientBootstrapSaveEndPacket(sha256Hash));
                    OnMainThread.Enqueue(() =>
                    {
                        saveUploadStatus = "Upload finished. Waiting for server to confirm and shut down...";
                    });
                }
                catch (Exception e)
                {
                    OnMainThread.Enqueue(() =>
                    {
                        isUploadingSave = false;
                        saveUploadStatus = $"Failed to upload save.zip: {e.GetType().Name}: {e.Message}";
                    });
                }
            }) { IsBackground = true, Name = "MP Bootstrap save upload" }.Start();
        }
    }
}