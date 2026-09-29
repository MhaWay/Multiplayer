using Multiplayer.Common;

namespace Tests;

[TestFixture]
public class FreezeManagerTest
{
    private MultiplayerServer server = null!;
    private int nextPlayerId;

    [SetUp]
    public void SetUp()
    {
        server = MultiplayerServer.instance = new MultiplayerServer(new ServerSettings
        {
            gameName = "Test",
            direct = false,
            lan = false
        });
        nextPlayerId = 1;
    }

    [TearDown]
    public void TearDown()
    {
        MultiplayerServer.instance = null;
    }

    private ServerPlayer AddPlayer(string username, bool isHost = false)
    {
        var conn = new DummyConnection(username);
        var player = new ServerPlayer(nextPlayerId++, conn);
        conn.serverPlayer = player;
        conn.ChangeState(ConnectionStateEnum.ServerPlaying);

        if (isHost)
            server.hostUsername = username;

        server.playerManager.Players.Add(player);
        return player;
    }

    private void RemovePlayer(ServerPlayer player)
    {
        server.playerManager.Players.Remove(player);
    }

    [Test]
    public void HostPresent_HostFrozen_ServerFreezes()
    {
        var host = AddPlayer("host", isHost: true);
        host.frozen = true;

        server.freezeManager.Tick();

        Assert.That(server.freezeManager.Frozen, Is.True);
    }

    [Test]
    public void HostPresent_HostNotFrozen_ServerNotFrozen()
    {
        var host = AddPlayer("host", isHost: true);
        host.frozen = false;

        server.freezeManager.Tick();

        Assert.That(server.freezeManager.Frozen, Is.False);
    }

    [Test]
    public void HostAbsent_PlayersPresent_NotFrozen()
    {
        // Host was never added — only a non-host player is present
        AddPlayer("player1");

        // Force frozen state to true to verify it gets unfrozen
        // Use Tick with a temporary host to set Frozen = true
        var tempHost = AddPlayer("host", isHost: true);
        tempHost.frozen = true;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.True);

        // Now remove host — simulating disconnect
        RemovePlayer(tempHost);
        server.hostUsername = "host"; // host username stays but player is gone

        server.freezeManager.Tick();

        Assert.That(server.freezeManager.Frozen, Is.False);
    }

    [Test]
    public void HostAbsent_NoPlayers_StaysFrozen()
    {
        // Set Frozen = true by having a host freeze, then removing everyone
        var host = AddPlayer("host", isHost: true);
        host.frozen = true;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.True);

        RemovePlayer(host);

        server.freezeManager.Tick();

        Assert.That(server.freezeManager.Frozen, Is.True);
    }

    [Test]
    public void HostAbsent_NoPlayers_NotFrozenStaysNotFrozen()
    {
        // No players at all, not frozen — should remain not frozen
        // (no one to send the freeze packet to anyway)
        server.freezeManager.Tick();

        Assert.That(server.freezeManager.Frozen, Is.False);
    }

    [Test]
    public void StaticInstanceNull_PlayerPresent_DoesNotThrow()
    {
        // Reproduces issue #991: during server shutdown TryStop() sets the static
        // MultiplayerServer.instance to null while the server thread's current loop
        // iteration is still inside FreezeManager.Tick. The old p => p.IsHost lambda
        // dereferenced the static instance (Server property), throwing NRE that froze
        // the loop. The fix resolves "is this the host?" locally against our captured
        // Server field so the tick is crash-safe.
        var host = AddPlayer("host", isHost: true);
        host.frozen = true;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.True);

        var savedInstance = MultiplayerServer.instance;
        try
        {
            MultiplayerServer.instance = null;

            // Before the fix the FirstOrDefault lambda threw NRE inside get_IsHost
            // (Server => instance!), aborting FreezeManager.Tick and starving the
            // server loop. Now it must run cleanly.
            Assert.DoesNotThrow(() => server.freezeManager.Tick(),
                "Tick must not throw when the static instance is null (shutdown race)");
        }
        finally
        {
            MultiplayerServer.instance = savedInstance;
        }
    }

    [Test]
    public void HostReconnects_ResumesNormalBehavior()
    {
        // Start with host, freeze
        var host = AddPlayer("host", isHost: true);
        host.frozen = true;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.True);

        // Host disconnects, player present → unfreezes
        RemovePlayer(host);
        var player = AddPlayer("player1");
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.False);

        // Host reconnects and is not frozen → stays unfrozen
        var hostAgain = AddPlayer("host", isHost: true);
        hostAgain.frozen = false;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.False);

        // Host freezes again → server freezes
        hostAgain.frozen = true;
        server.freezeManager.Tick();
        Assert.That(server.freezeManager.Frozen, Is.True);
    }
}
