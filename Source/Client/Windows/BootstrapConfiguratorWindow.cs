using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using LiteNetLib;
using Multiplayer.Client.Comp;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using Multiplayer.Common.Util;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Multiplayer.Client
{
    /// <summary>
    /// Shown when connecting to a server that's in bootstrap/configuration mode.
    /// Guides the user through uploading settings.toml (if needed) and then save.zip.
    /// </summary>
    public class BootstrapConfiguratorWindow : Window
    {
        private readonly ConnectionBase connection;
        private string serverAddress;
        private int serverPort;
        private bool isReconnecting;
        private int reconnectCheckTimer;

        private ServerSettings settings;

        private enum Step
        {
            Settings,
            GenerateMap
        }

        private enum Tab
        {
            Connecting,
            Gameplay,
            Preview
        }

        private Step step;
        private Tab tab;

        // UI buffers
        private ServerSettingsUI.BufferSet settingsUiBuffers = new();

        // toml preview
        private string tomlPreview;
        private Vector2 tomlScroll;

        private bool isUploadingToml;
        private float uploadProgress;
        private string statusText;
        private bool settingsUploaded;

        // Save.zip upload
        private bool isUploadingSave;
        private float saveUploadProgress;
        private string saveUploadStatus;
        private static string lastSavedReplayPath;
        private static bool lastSaveReady;

        // Autosave trigger (once) during bootstrap map generation
        private bool saveReady;
        private string savedReplayPath;

        private const string BootstrapSaveName = "Bootstrap";
        private bool saveUploadAutoStarted;
        private bool autoUploadAttempted;

        // Vanilla page auto-advance during bootstrap
        private bool autoAdvanceArmed;

        // Delay before saving after entering the map
        private float postMapEnterSaveDelayRemaining;
        private const float PostMapEnterSaveDelaySeconds = 1f;

        // Ensure we don't queue multiple saves.
        private bool bootstrapSaveQueued;

        // After entering a map, wait until at least one controllable colonist pawn exists.
        private bool awaitingControllablePawns;
        private float awaitingControllablePawnsElapsed;
        private const float AwaitControllablePawnsTimeoutSeconds = 30f;

        // Static flag to track bootstrap map initialization
        public static bool AwaitingBootstrapMapInit;
        public static BootstrapConfiguratorWindow Instance;

        // Hide window during map generation/tile selection
        private bool hideWindowDuringMapGen;

        private const float LabelWidth = 110f;
        private const int MaxGameNameLength = 70;

        public override Vector2 InitialSize => new(550f, 620f);

        public BootstrapConfiguratorWindow(ConnectionBase connection)
        {
            this.connection = connection;
            Instance = this;

            // Save server address for reconnection after world generation
            serverAddress = Multiplayer.session?.address;
            serverPort = Multiplayer.session?.port ?? 0;

            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            forcePause = false;

            // Initialize with reasonable defaults for standalone/headless server
            settings = new ServerSettings
            {
                gameName = $"{Multiplayer.username}'s Server",
                direct = true,
                lan = false,
                steam = false,
                arbiter = false,
                maxPlayers = 8,
                autosaveInterval = 1,
                autosaveUnit = AutosaveUnit.Days
            };

            // Initialize UI buffers
            settingsUiBuffers.MaxPlayersBuffer = settings.maxPlayers.ToString();
            settingsUiBuffers.AutosaveBuffer = settings.autosaveInterval.ToString();

            // Choose the initial step based on what the server told us.
            step = Multiplayer.session?.serverBootstrapSettingsMissing == true ? Step.Settings : Step.GenerateMap;

            statusText = step == Step.Settings
                ? "Server settings.toml is missing. Configure and upload it."
                : "Server settings.toml is already configured.";

            // Check if we have a previously saved Bootstrap.zip from this session (reconnect case)
            if (!autoUploadAttempted && lastSaveReady && !string.IsNullOrEmpty(lastSavedReplayPath) && File.Exists(lastSavedReplayPath))
            {
                savedReplayPath = lastSavedReplayPath;
                saveReady = true;
                saveUploadStatus = "Save ready from previous session. Uploading...";
                saveUploadAutoStarted = true;
                autoUploadAttempted = true;
                StartUploadSaveZip();
            }

            RebuildTomlPreview();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.UpperCenter;

            // Title
            Widgets.Label(inRect.Down(0), "Server Bootstrap Configuration");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;

            var entry = new Rect(0, 45, inRect.width, 30f);
            entry.xMin += 4;

            // Game name
            settings.gameName = MpUI.TextEntryLabeled(entry, $"{"MpGameName".Translate()}:  ", settings.gameName, LabelWidth);
            if (settings.gameName.Length > MaxGameNameLength)
                settings.gameName = settings.gameName.Substring(0, MaxGameNameLength);

            entry = entry.Down(40);

            if (step == Step.Settings)
                DrawSettings(entry, inRect);
            else
                DrawGenerateMap(entry, inRect);
        }

        private void DrawGenerateMap(Rect entry, Rect inRect)
        {
            // Status text
            Text.Font = GameFont.Small;
            var statusHeight = Text.CalcHeight(statusText ?? "", entry.width);
            Widgets.Label(entry.Height(statusHeight), statusText ?? "");
            entry = entry.Down(statusHeight + 10);

            // Important notice about faction ownership
            if (!AwaitingBootstrapMapInit && !saveReady && !isUploadingSave && !isReconnecting)
            {
                var noticeRect = entry.Height(100f);
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
                entry = entry.Down(110);
            }

            // Save upload status
            if (!string.IsNullOrEmpty(saveUploadStatus))
            {
                var saveStatusHeight = Text.CalcHeight(saveUploadStatus, entry.width);
                Widgets.Label(entry.Height(saveStatusHeight), saveUploadStatus);
                entry = entry.Down(saveStatusHeight + 4);
            }

            // Progress bar
            if (autoAdvanceArmed || isUploadingSave)
            {
                var barRect = entry.Height(18f);
                Widgets.FillableBar(barRect, isUploadingSave ? saveUploadProgress : 0.1f);
                entry = entry.Down(24);
            }

            // Generate map button
            bool showGenerateButton = !(autoAdvanceArmed || AwaitingBootstrapMapInit || saveReady || isUploadingSave || isReconnecting);
            if (showGenerateButton)
            {
                var buttonRect = new Rect((inRect.width - 200f) / 2f, inRect.height - 45f, 200f, 40f);
                if (Widgets.ButtonText(buttonRect, "Generate map"))
                {
                    saveUploadAutoStarted = false;
                    hideWindowDuringMapGen = true;
                    StartVanillaNewColonyFlow();
                }
            }

            // Auto-start upload when save is ready
            if (saveReady && !isUploadingSave && !saveUploadAutoStarted && Multiplayer.Client == null && Current.ProgramState != ProgramState.Playing)
            {
                saveUploadAutoStarted = true;
                ReconnectAndUploadSave();
            }
        }

        private void DrawSettings(Rect entry, Rect inRect)
        {
            // Status + progress
            if (!string.IsNullOrEmpty(statusText))
            {
                var statusHeight = Text.CalcHeight(statusText, entry.width);
                Widgets.Label(entry.Height(statusHeight), statusText);
                entry = entry.Down(statusHeight + 4);
            }

            if (isUploadingToml)
            {
                var barRect = entry.Height(20f);
                Widgets.FillableBar(barRect, uploadProgress);
                entry = entry.Down(24);
            }

            // Tab buttons
            using (MpStyle.Set(TextAnchor.MiddleLeft))
            {
                DoTabButton(entry.Width(140).Height(40f), Tab.Connecting);
                DoTabButton(entry.Down(50f).Width(140).Height(40f), Tab.Gameplay);
                if (Prefs.DevMode)
                    DoTabButton(entry.Down(100f).Width(140).Height(40f), Tab.Preview);
            }

            // Content based on selected tab
            var contentRect = entry.MinX(entry.xMin + 150);
            var buffers = new ServerSettingsUI.BufferSet
            {
                MaxPlayersBuffer = settingsUiBuffers.MaxPlayersBuffer,
                AutosaveBuffer = settingsUiBuffers.AutosaveBuffer
            };

            if (tab == Tab.Connecting)
                ServerSettingsUI.DrawNetworkingSettings(contentRect, settings, buffers);
            else if (tab == Tab.Gameplay)
                ServerSettingsUI.DrawGameplaySettingsOnly(contentRect, settings, buffers);
            else if (tab == Tab.Preview)
            {
                RebuildTomlPreview();
                var previewRect = new Rect(contentRect.x, contentRect.y, contentRect.width, inRect.height - contentRect.y - 50f);
                DrawTomlPreview(previewRect);
            }

            // Sync buffers back
            settingsUiBuffers.MaxPlayersBuffer = buffers.MaxPlayersBuffer;
            settingsUiBuffers.AutosaveBuffer = buffers.AutosaveBuffer;

            // Buttons at bottom
            DrawSettingsButtons(new Rect(0, inRect.height - 40f, inRect.width, 35f));
        }

        private void DoTabButton(Rect r, Tab tab)
        {
            Widgets.DrawOptionBackground(r, tab == this.tab);
            if (Widgets.ButtonInvisible(r, true))
            {
                this.tab = tab;
                SoundDefOf.Click.PlayOneShotOnCamera();
            }

            float num = r.x + 10f;
            Texture2D icon;
            string label;

            if (tab == Tab.Connecting)
            {
                icon = ContentFinder<Texture2D>.Get("UI/Icons/Options/OptionsGeneral");
                label = "MpHostTabConnecting".Translate();
            }
            else if (tab == Tab.Gameplay)
            {
                icon = ContentFinder<Texture2D>.Get("UI/Icons/Options/OptionsGameplay");
                label = "MpHostTabGameplay".Translate();
            }
            else
            {
                icon = null;
                label = "Preview";
            }

            if (icon != null)
            {
                Rect rect = new Rect(num, r.y + (r.height - 20f) / 2f, 20f, 20f);
                GUI.DrawTexture(rect, icon);
                num += 30f;
            }

            Widgets.Label(new Rect(num, r.y, r.width - num, r.height), label);
        }

        private void DrawSettingsButtons(Rect inRect)
        {
            Rect nextRect;
            if (Prefs.DevMode)
            {
                var copyRect = new Rect(inRect.x, inRect.y, 150f, inRect.height);
                if (Widgets.ButtonText(copyRect, "Copy TOML"))
                {
                    RebuildTomlPreview();
                    GUIUtility.systemCopyBuffer = tomlPreview;
                    Messages.Message("Copied settings.toml to clipboard", MessageTypeDefOf.SilentInput, false);
                }
                nextRect = new Rect(inRect.xMax - 150f, inRect.y, 150f, inRect.height);
            }
            else
            {
                nextRect = new Rect((inRect.width - 150f) / 2f, inRect.y, 150f, inRect.height);
            }

            var nextLabel = settingsUploaded ? "Uploaded" : "Next";
            var nextEnabled = !isUploadingToml && !settingsUploaded;

            var prevColor = GUI.color;
            if (!nextEnabled)
                GUI.color = new Color(1f, 1f, 1f, 0.5f);

            if (Widgets.ButtonText(nextRect, nextLabel))
            {
                if (nextEnabled)
                {
                    RebuildTomlPreview();
                    StartUploadSettingsToml();
                }
            }

            GUI.color = prevColor;
        }

        private void StartUploadSettingsToml()
        {
            isUploadingToml = true;
            uploadProgress = 0f;
            statusText = "Uploading server settings...";

            new System.Threading.Thread(() =>
            {
                try
                {
                    connection.Send(new ClientBootstrapSettingsPacket(settings));

                    OnMainThread.Enqueue(() =>
                    {
                        isUploadingToml = false;
                        settingsUploaded = true;
                        statusText = "Server settings configured correctly. Proceed with map generation.";
                        step = Step.GenerateMap;

                        // Clear the flag so WindowUpdate() doesn't reset step back to Settings
                        if (Multiplayer.session != null)
                            Multiplayer.session.serverBootstrapSettingsMissing = false;
                    });
                }
                catch (Exception e)
                {
                    Log.Error($"Bootstrap settings upload failed: {e}");
                    OnMainThread.Enqueue(() =>
                    {
                        isUploadingToml = false;
                        statusText = $"Failed to upload settings: {e.GetType().Name}: {e.Message}";
                    });
                }
            }) { IsBackground = true, Name = "MP Bootstrap settings upload" }.Start();
        }

        private void StartVanillaNewColonyFlow()
        {
            // Disconnect from server before world generation to avoid sync conflicts.
            // We'll reconnect after the autosave is complete to upload save.zip.
            if (Multiplayer.session != null)
            {
                Multiplayer.session.Stop();
                Multiplayer.session = null;
            }

            try
            {
                Current.Game ??= new Game();
                Current.Game.InitData ??= new GameInitData { startedFromEntry = true };

                // Ensure BootstrapCoordinator is added to the game components
                if (Current.Game.components.All(c => c is not BootstrapCoordinator))
                {
                    Current.Game.components.Add(new BootstrapCoordinator(Current.Game));
                }

                var scenarioPage = new Page_SelectScenario();
                Find.WindowStack.Add(PageUtility.StitchedPages(new System.Collections.Generic.List<Page> { scenarioPage }));

                // Start watching for page flow + map entry
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

            // Once a local hosted MP session exists, bootstrap map-init detection must stop.
            if (Multiplayer.Client != null || bootstrapSaveQueued || saveReady || isUploadingSave || isReconnecting || saveUploadAutoStarted)
                return;

            try
            {
                if (LongEventHandler.AnyEventNowOrWaiting)
                    return;
            }
            catch
            {
                // If the API isn't available, fail open.
            }

            if (Current.ProgramState != ProgramState.Playing)
                return;

            if (Find.Maps == null || Find.Maps.Count == 0)
                return;

            AwaitingBootstrapMapInit = true;
            saveUploadStatus = "Entered map. Waiting for initialization to complete...";

            // Stop page driver
            autoAdvanceArmed = false;

            // Re-add to WindowStack if missing (Root_Play transition clears all windows)
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

            // Also drive the post-map save pipeline from this reliable update loop
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

            // Skip if timer was never started (default 0) AND we're not waiting for pawns
            if (postMapEnterSaveDelayRemaining <= 0f && !awaitingControllablePawns)
                return;

            postMapEnterSaveDelayRemaining -= Time.deltaTime;

            if (postMapEnterSaveDelayRemaining > 0f)
                return;

            // Wait until we actually have spawned pawns
            if (awaitingControllablePawns)
            {
                awaitingControllablePawnsElapsed += Time.deltaTime;

                if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                {
                    var anyColonist = false;
                    try
                    {
                        anyColonist = Find.CurrentMap.mapPawns?.FreeColonistsSpawned != null &&
                                      Find.CurrentMap.mapPawns.FreeColonistsSpawned.Count > 0;
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

                if (awaitingControllablePawns)
                {
                    if (awaitingControllablePawnsElapsed > AwaitControllablePawnsTimeoutSeconds)
                    {
                        awaitingControllablePawns = false;
                        Log.Warning("Timed out waiting for controllable pawns during bootstrap; saving anyway");
                    }
                    else
                    {
                        saveUploadStatus = "Waiting for controllable colonists to spawn...";
                        return;
                    }
                }
            }

            postMapEnterSaveDelayRemaining = 0f;
            bootstrapSaveQueued = true;
            saveUploadStatus = "Map initialized. Starting hosted MP session...";

            LongEventHandler.QueueLongEvent(() =>
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

                    bool hosted = HostWindow.HostProgrammatically(hostSettings);
                    if (!hosted)
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

                        LongEventHandler.QueueLongEvent(() =>
                        {
                            try
                            {
                                Autosaving.SaveGameToFile_Overwrite(BootstrapSaveName, currentReplay: false);

                                var path = Path.Combine(Multiplayer.ReplaysDir, $"{BootstrapSaveName}.zip");

                                OnMainThread.Enqueue(() =>
                                {
                                    savedReplayPath = path;
                                    saveReady = File.Exists(savedReplayPath);
                                    lastSavedReplayPath = savedReplayPath;
                                    lastSaveReady = saveReady;

                                    if (saveReady)
                                    {
                                        // Prevent DrawGenerateMap from reconnecting before we've returned to the menu.
                                        saveUploadAutoStarted = true;
                                        saveUploadStatus = "Save created. Returning to menu...";

                                        LongEventHandler.QueueLongEvent(() =>
                                        {
                                            GenScene.GoToMainMenu();

                                            OnMainThread.Enqueue(() =>
                                            {
                                                saveUploadStatus = "Reconnecting to upload save...";
                                                ReconnectAndUploadSave();
                                            });
                                        }, "Returning to menu", false, null);
                                    }
                                    else
                                    {
                                        saveUploadStatus = $"Save finished but file not found: {path}";
                                        Log.Error($"Bootstrap save finished but file was missing: {savedReplayPath}");
                                        bootstrapSaveQueued = false;
                                    }
                                });
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
                        }, "Saving", false, null);
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
            }, "Starting host", false, null);
        }

        public override void PreOpen()
        {
            base.PreOpen();
            UpdateWindowVisibility();
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();

            UpdateWindowVisibility();

            // Always try to drive the save delay
            TickPostMapEnterSaveDelayAndMaybeSave();

            // Poll LiteNet during reconnection
            if (isReconnecting && Multiplayer.session?.netClient != null)
                Multiplayer.session.netClient.PollEvents();

            if (isReconnecting)
                CheckReconnectionState();

            // If we've reconnected and server indicates settings are missing, reset to settings step
            if (!isReconnecting && Multiplayer.session?.serverBootstrapSettingsMissing == true && step == Step.GenerateMap)
            {
                step = Step.Settings;
                settingsUploaded = false;
                statusText = "Server settings.toml is missing. Configure and upload it.";
            }
        }

        private void UpdateWindowVisibility()
        {
            if (hideWindowDuringMapGen)
            {
                windowRect.width = 0;
                windowRect.height = 0;
            }
            else
            {
                var size = InitialSize;
                if (windowRect.width == 0)
                {
                    windowRect.width = size.x;
                    windowRect.height = size.y;
                    windowRect.x = (UI.screenWidth - size.x) / 2f;
                    windowRect.y = (UI.screenHeight - size.y) / 2f;
                }
            }
        }

        /// <summary>
        /// Called by <see cref="BootstrapCoordinator"/> once per second while the bootstrap window exists.
        /// </summary>
        internal void BootstrapCoordinatorTick()
        {
            if (!AwaitingBootstrapMapInit)
                TryArmAwaitingBootstrapMapInit();

            TickPostMapEnterSaveDelayAndMaybeSave();
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
                netClient.Connect(serverAddress, serverPort, "");

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
            else if (reconnectCheckTimer > 600) // ~10 seconds at 60fps
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

        private void DrawTomlPreview(Rect inRect)
        {
            Widgets.DrawMenuSection(inRect);
            var inner = inRect.ContractedBy(10f);

            Text.Font = GameFont.Small;
            Widgets.Label(inner.TopPartPixels(22f), "settings.toml preview");

            var previewRect = new Rect(inner.x, inner.y + 26f, inner.width, inner.height - 26f);
            var content = tomlPreview ?? "";

            var viewRect = new Rect(0f, 0f, previewRect.width - 16f, Mathf.Max(previewRect.height, Text.CalcHeight(content, previewRect.width - 16f) + 20f));
            Widgets.BeginScrollView(previewRect, ref tomlScroll, viewRect);
            Widgets.Label(new Rect(0f, 0f, viewRect.width, viewRect.height), content);
            Widgets.EndScrollView();
        }

        private void RebuildTomlPreview()
        {
            var sb = new StringBuilder();

            sb.AppendLine("# Generated by Multiplayer bootstrap configurator");
            sb.AppendLine("# Keys must match ServerSettings.ExposeData()\n");

            AppendKv(sb, "directAddress", settings.directAddress);
            AppendKv(sb, "maxPlayers", settings.maxPlayers);
            AppendKv(sb, "autosaveInterval", settings.autosaveInterval);
            AppendKv(sb, "autosaveUnit", settings.autosaveUnit.ToString());
            AppendKv(sb, "steam", settings.steam);
            AppendKv(sb, "direct", settings.direct);
            AppendKv(sb, "lan", settings.lan);
            AppendKv(sb, "asyncTime", settings.asyncTime);
            AppendKv(sb, "multifaction", settings.multifaction);
            AppendKv(sb, "debugMode", settings.debugMode);
            AppendKv(sb, "desyncTraces", settings.desyncTraces);
            AppendKv(sb, "syncConfigs", settings.syncConfigs);
            AppendKv(sb, "autoJoinPoint", settings.autoJoinPoint.ToString());
            AppendKv(sb, "devModeScope", settings.devModeScope.ToString());
            AppendKv(sb, "hasPassword", settings.hasPassword);
            AppendKv(sb, "password", settings.password ?? "");
            AppendKv(sb, "pauseOnLetter", settings.pauseOnLetter.ToString());
            AppendKv(sb, "pauseOnJoin", settings.pauseOnJoin);
            AppendKv(sb, "pauseOnDesync", settings.pauseOnDesync);
            AppendKv(sb, "timeControl", settings.timeControl.ToString());

            tomlPreview = sb.ToString();
        }

        private static void AppendKv(StringBuilder sb, string key, string value)
        {
            sb.Append(key);
            sb.Append(" = ");
            var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            sb.Append('"').Append(escaped).Append('"');
            sb.AppendLine();
        }

        private static void AppendKv(StringBuilder sb, string key, bool value)
        {
            sb.Append(key);
            sb.Append(" = ");
            sb.AppendLine(value ? "true" : "false");
        }

        private static void AppendKv(StringBuilder sb, string key, int value)
        {
            sb.Append(key);
            sb.Append(" = ");
            sb.AppendLine(value.ToString());
        }

        private static void AppendKv(StringBuilder sb, string key, float value)
        {
            sb.Append(key);
            sb.Append(" = ");
            sb.AppendLine(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
