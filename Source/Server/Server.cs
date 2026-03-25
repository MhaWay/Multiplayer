using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Multiplayer.Common;
using Multiplayer.Common.Util;
using Server;

ServerLog.detailEnabled = true;
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

const string settingsFile = "settings.toml";
const string stopCmd = "stop";
const string saveFile = "save.zip";

var settings = new ServerSettings
{
    direct = true,
    lan = false
};

if (File.Exists(settingsFile))
{
    settings = TomlSettingsCommon.Load(settingsFile);
    if (settings.lan) settings.lanAddress = GetLocalIpAddress() ?? "127.0.0.1";
}
else
    ServerLog.Log($"Bootstrap mode: '{settingsFile}' not found. Waiting for a client to upload it.");

var server = MultiplayerServer.instance = new MultiplayerServer(settings)
{
    running = true,
    initDataState = InitDataState.Waiting
};

var bootstrap = !File.Exists(settingsFile);

var consoleSource = new ConsoleSource();

if (!bootstrap && File.Exists(saveFile))
{
    LoadSave(server, saveFile);
}
else
{
    bootstrap = true;
    ServerLog.Log($"Bootstrap mode: '{saveFile}' not found. Server will start without a loaded save.");
    ServerLog.Log("Waiting for a client to upload world data.");
}
server.BootstrapMode = bootstrap;

if (bootstrap)
    ServerLog.Detail("Bootstrap flag is enabled.");

server.liteNet.StartNet();

new Thread(server.Run) { Name = "Server thread" }.Start();

// In bootstrap mode we keep the server alive and wait for any client to connect.
// The actual world data upload is handled by the normal networking code paths.
if (bootstrap)
    BootstrapMode.WaitForClient(server, CancellationToken.None);

// If bootstrap mode completed (a client uploaded save.zip) the server thread will have set
// server.running = false. In that case, exit so the user can restart the server normally.
if (bootstrap && !server.running)
    return;

while (true)
{
    var cmd = Console.ReadLine();
    if (cmd != null)
        server.Enqueue(() => server.HandleChatCmd(consoleSource, cmd));

    if (cmd == stopCmd)
        break;
}

static void LoadSave(MultiplayerServer server, string path)
{
    using var zip = ZipFile.OpenRead(path);

    var replayInfo = ReplayInfo.Read(zip.GetBytes("info"));
    ServerLog.Detail($"Loading {path} saved in RW {replayInfo.rwVersion} with {replayInfo.modNames.Count} mods");

    server.settings.gameName = replayInfo.name;
    server.worldData.hostFactionId = replayInfo.playerFaction;

    //This parses multiple saves as long as they are named correctly
    server.gameTimer = replayInfo.sections[0].start;
    server.startingTimer = replayInfo.sections[0].start;


    server.worldData.savedGame = Compress(zip.GetBytes("world/000_save"));

    // Parse cmds entry for each map
    foreach (var entry in zip.GetEntries("maps/*_cmds"))
    {
        var parts = entry.FullName.Split(new[] { '_' });

        if (parts.Length == 3)
        {
            int mapNumber = int.Parse(parts[1]);
            server.worldData.mapCmds[mapNumber] = ScheduledCommand.DeserializeCmds(zip.GetBytes(entry.FullName)).Select(ScheduledCommand.Serialize).ToList();
        }
    }

    // Parse save entry for each map
    foreach (var entry in zip.GetEntries("maps/*_save"))
    {
        var parts = entry.FullName.Split(new[] { '_' });

        if (parts.Length == 3)
        {
            int mapNumber = int.Parse(parts[1]);
            server.worldData.mapData[mapNumber] = Compress(zip.GetBytes(entry.FullName));
        }
    }
    

    server.worldData.mapCmds[-1] = ScheduledCommand.DeserializeCmds(zip.GetBytes("world/000_cmds")).Select(ScheduledCommand.Serialize).ToList();
    server.worldData.sessionData = Array.Empty<byte>();
}

static byte[] Compress(byte[] input)
{
    using var result = new MemoryStream();

    using (var compressionStream = new GZipStream(result, CompressionMode.Compress))
    {
        compressionStream.Write(input, 0, input.Length);
        compressionStream.Flush();

    }
    return result.ToArray();
}

static string GetLocalIpAddress()
{
    try
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.IP))
        {
            socket.Connect("8.8.8.8", 65530);
            var endPoint = socket.LocalEndPoint as IPEndPoint;
            return endPoint.Address.ToString();
        }
    }
    catch
    {
        return Dns.GetHostEntry(Dns.GetHostName()).AddressList.FirstOrDefault(i => i.AddressFamily == AddressFamily.InterNetwork)?.ToString();
    }
}

class ConsoleSource : IChatSource
{
    public void SendMsg(string msg)
    {
        ServerLog.Log(msg);
    }
}
