using System;
using System.IO;
using Multiplayer.Client.Networking;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using UnityEngine;
using Verse;

namespace Multiplayer.Client
{
    /// <summary>
    /// Shown when connecting to a server that's in bootstrap/configuration mode.
    /// Guides the user through uploading settings.toml (if needed) and then save.zip.
    /// </summary>
    public partial class BootstrapConfiguratorWindow : Window
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

    }
}
