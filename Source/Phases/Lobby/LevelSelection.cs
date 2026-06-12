using System.Collections.Generic;
using System.Linq;
using Celeste.Mod.UltimateMadelineCeleste.Entities;
using Celeste.Mod.UltimateMadelineCeleste.Network;
using Celeste.Mod.UltimateMadelineCeleste.Network.Messages;
using Celeste.Mod.UltimateMadelineCeleste.Players;
using Celeste.Mod.UltimateMadelineCeleste.Session;
using Celeste.Mod.UltimateMadelineCeleste.Utilities;
using Microsoft.Xna.Framework;
using Monocle;
using Steamworks;

namespace Celeste.Mod.UltimateMadelineCeleste.Phases.Lobby;

/// <summary>
/// Handles level selection: tracking players on buttons and countdown to level start.
/// </summary>
public class LevelSelection
{
    private const float CountdownDuration = 3f;
    private const float GoDisplayDuration = 1f;
    private const float ArrowEliminationInterval = 1f;
    private const float CameraZoomDuration = 1.5f;

    private readonly LobbyPhase _lobby;
    private PlayersController _players;

    private LevelVotingCountdown _countdownUI;
    private bool _countdownActive;
    private float _countdownTimer;
    private bool _showingGo;
    private float _goTimer;
    private string _selectedMapSid;

    // Arrow elimination sequence state
    private bool _eliminationActive;
    private float _eliminationTimer;
    private LevelButton _winningButton;
    private List<UmcPlayer> _losingPlayers; // Players not on the winning button

    // Camera zoom state
    private bool _cameraZooming;
    private float _cameraZoomTimer;

    public bool IsCountdownActive => _countdownActive || _showingGo || _eliminationActive || _cameraZooming;

    private bool IsHost => NetworkManager.Instance?.IsHost ?? true;

    public LevelSelection(LobbyPhase lobby, PlayersController players)
    {
        _lobby = lobby;
        _players = players;
        
        NetworkManager.Handle<LevelVoteStateMessage>(HandleVoteState);
        NetworkManager.Handle<LevelVoteEliminateMessage>(HandleVoteEliminate);
        NetworkManager.Handle<LoadLevelMessage>(HandleLoadLevel);
    }

    private void HandleVoteState(CSteamID sender, LevelVoteStateMessage message)
    {
        // Only clients handle this
        if (IsHost) return;

        _selectedMapSid = message.WinningMapSID;

        switch (message.Phase)
        {
            case LevelVotePhase.Countdown:
                if (!_countdownActive)
                {
                    _countdownActive = true;
                    if (_countdownUI == null)
                    {
                        _countdownUI = new LevelVotingCountdown();
                        _lobby.Scene.Add(_countdownUI);
                    }
                    _countdownUI.Show(message.CountdownNumber);
                }
                else
                {
                    _countdownUI?.UpdateNumber(message.CountdownNumber);
                }
                break;

            case LevelVotePhase.Go:
                _countdownActive = false;
                _showingGo = true;
                _countdownUI?.ShowGo();

                // Freeze inputs on client too
                if (PlayerSpawner.Instance != null)
                    PlayerSpawner.Instance.InputsFrozen = true;
                if (LobbyPhase.Instance != null)
                    LobbyPhase.Instance.IsLevelTransitioning = true;
                break;

            case LevelVotePhase.Eliminating:
                _showingGo = false;
                _eliminationActive = true;
                _countdownUI?.Hide();
                break;

            case LevelVotePhase.CameraZoom:
                _eliminationActive = false;
                _cameraZooming = true;

                // Start camera zoom on client
                Vector2 buttonCenter = message.WinningButtonPosition + new Vector2(32, 24);
                CameraController.Instance?.SetFocusTarget(buttonCenter, 1.2f);
                break;

            case LevelVotePhase.Complete:
                _cameraZooming = false;
                CameraController.Instance?.ClearFocusTarget();
                break;

            case LevelVotePhase.None:
                // Reset all state
                _countdownActive = false;
                _showingGo = false;
                _eliminationActive = false;
                _cameraZooming = false;
                _countdownUI?.Hide();

                // Unfreeze inputs in case the transition was cancelled after Go
                if (PlayerSpawner.Instance != null)
                    PlayerSpawner.Instance.InputsFrozen = false;
                if (LobbyPhase.Instance != null)
                    LobbyPhase.Instance.IsLevelTransitioning = false;

                CameraController.Instance?.ClearFocusTarget();
                break;
        }
    }

    private void HandleVoteEliminate(CSteamID sender, LevelVoteEliminateMessage message)
    {
        // Only clients handle this
        if (IsHost) return;

        if (_lobby.Scene is Level level)
        {
            // Find the button with this player's arrow and hide it
            var levelButtons = level.Tracker.GetEntities<LevelButton>().Cast<LevelButton>().ToList();
            foreach (var button in levelButtons)
            {
                if (button.HasArrowForPlayer(message.PlayerSlotIndex))
                {
                    button.HideArrow(message.PlayerSlotIndex);
                    Audio.Play("event:/ui/main/button_toggle_off");
                    break;
                }
            }
        }
    }

    private void HandleLoadLevel(LoadLevelMessage message)
    {
        UmcLogger.Info($"Loading level: {message.MapSid}");
        PlayerSpawner.Instance?.DespawnAllSessionPlayers();
        PhaseManager.Instance?.TransitionToLevel(message.MapSid);
    }

    private void BroadcastVoteState(LevelVotePhase phase, int countdownNumber = 0)
    {
        if (!IsHost) return;

        var net = NetworkManager.Instance;
        if (net?.IsOnline != true) return;

        net.Messages.Broadcast(new LevelVoteStateMessage
        {
            Phase = phase,
            CountdownNumber = countdownNumber,
            WinningButtonPosition = _winningButton?.Position ?? Vector2.Zero,
            WinningMapSID = _selectedMapSid ?? ""
        });
    }

    private void BroadcastEliminate(int playerSlotIndex)
    {
        if (!IsHost) return;

        var net = NetworkManager.Instance;
        if (net?.IsOnline != true) return;

        net.Messages.Broadcast(new LevelVoteEliminateMessage
        {
            PlayerSlotIndex = playerSlotIndex
        });
    }

    public void Cleanup()
    {
        _players = null;
        _countdownUI?.RemoveSelf();
        _countdownUI = null;

        // Reset state
        if (PlayerSpawner.Instance != null)
            PlayerSpawner.Instance.InputsFrozen = false;

        CameraController.Instance?.ClearFocusTarget();
    }

    public void Update(Level level)
    {
        if (_cameraZooming)
        {
            UpdateCameraZoom(level);
            return;
        }

        if (_eliminationActive)
        {
            if (IsHost) UpdateArrowElimination(level);
            return;
        }

        if (_showingGo)
        {
            if (IsHost) UpdateGoDisplay(level);
            return;
        }

        if (_countdownActive)
        {
            if (IsHost) UpdateCountdown(level);
            return;
        }

        // Only host checks for players on buttons
        if (IsHost)
        {
            CheckForAllPlayersOnButtons(level);
        }
    }

    /// <summary>
    /// Checks if all players are standing on level buttons.
    /// </summary>
    private void CheckForAllPlayersOnButtons(Level level)
    {
        var session = GameSession.Instance;
        if (session == null) return;

        var allPlayers = session.Players.All;
        if (allPlayers.Count == 0) return;

        // Only count players who have selected a skin (are spawned)
        var activePlayers = allPlayers.Where(p => !string.IsNullOrEmpty(p.SkinId)).ToList();
        if (activePlayers.Count == 0) return;

        // Get all level buttons
        var levelButtons = level.Tracker.GetEntities<LevelButton>().Cast<LevelButton>().ToList();
        if (levelButtons.Count == 0) return;

        // Check which players are on buttons
        var playersOnButtons = new HashSet<int>();
        LevelButton mostPopularButton = null;
        int maxPlayers = 0;

        foreach (var button in levelButtons)
        {
            var playersOnThisButton = button.GetPlayersOnButton();
            foreach (int slot in playersOnThisButton)
            {
                playersOnButtons.Add(slot);
            }

            if (playersOnThisButton.Count > maxPlayers)
            {
                maxPlayers = playersOnThisButton.Count;
                mostPopularButton = button;
            }
        }

        // Check if all active players are on a button
        bool allOnButtons = activePlayers.All(p => playersOnButtons.Contains(p.SlotIndex));

        if (allOnButtons && mostPopularButton != null)
        {
            StartCountdown(mostPopularButton.MapSID);
        }
    }

    private void StartCountdown(string mapSID)
    {
        UmcLogger.Info($"Starting level selection countdown for: {mapSID}");

        _countdownActive = true;
        _countdownTimer = CountdownDuration;
        _selectedMapSid = mapSID;

        // Create countdown UI
        if (_countdownUI == null)
        {
            _countdownUI = new LevelVotingCountdown();
            _lobby.Scene.Add(_countdownUI);
        }

        int startNumber = (int)System.Math.Ceiling(_countdownTimer);
        _countdownUI.Show(startNumber);

        BroadcastVoteState(LevelVotePhase.Countdown, startNumber);
    }

    private int _lastBroadcastNumber = -1;

    private void UpdateCountdown(Level level)
    {
        // Check if any player stepped off
        if (!AreAllPlayersStillOnButtons(level))
        {
            CancelCountdown();
            return;
        }

        _countdownTimer -= Engine.DeltaTime;

        // Update UI with current second
        int currentSecond = (int)System.Math.Ceiling(_countdownTimer);
        _countdownUI?.UpdateNumber(currentSecond);

        // Broadcast number change
        if (currentSecond != _lastBroadcastNumber && currentSecond > 0)
        {
            _lastBroadcastNumber = currentSecond;
            BroadcastVoteState(LevelVotePhase.Countdown, currentSecond);
        }

        if (_countdownTimer <= 0f)
        {
            FinishCountdown();
        }
    }

    private bool AreAllPlayersStillOnButtons(Level level)
    {
        var session = GameSession.Instance;
        if (session == null) return false;

        var activePlayers = session.Players.All.Where(p => !string.IsNullOrEmpty(p.SkinId)).ToList();
        if (activePlayers.Count == 0) return false;

        var levelButtons = level.Tracker.GetEntities<LevelButton>().Cast<LevelButton>().ToList();

        var playersOnButtons = new HashSet<int>();
        foreach (var button in levelButtons)
        {
            foreach (int slot in button.GetPlayersOnButton())
            {
                playersOnButtons.Add(slot);
            }
        }

        return activePlayers.All(p => playersOnButtons.Contains(p.SlotIndex));
    }

    private void CancelCountdown()
    {
        UmcLogger.Info("Level selection countdown cancelled");

        _countdownActive = false;
        _countdownTimer = 0f;
        _selectedMapSid = null;
        _lastBroadcastNumber = -1;

        _countdownUI?.Hide();

        BroadcastVoteState(LevelVotePhase.None);
    }

    private void FinishCountdown()
    {
        UmcLogger.Info($"Level selection complete: {_selectedMapSid}");

        // Freeze player inputs and prevent joining
        if (PlayerSpawner.Instance != null)
            PlayerSpawner.Instance.InputsFrozen = true;

        LobbyPhase.Instance?.IsLevelTransitioning = true;

        _countdownActive = false;
        _showingGo = true;
        _goTimer = GoDisplayDuration;

        _countdownUI?.ShowGo();

        Audio.Play("event:/ui/main/button_select");

        BroadcastVoteState(LevelVotePhase.Go);
    }

    private void UpdateGoDisplay(Level level)
    {
        _goTimer -= Engine.DeltaTime;

        if (_goTimer <= 0f)
        {
            _showingGo = false;
            // _countdownUI?.Hide();

            // Check if all players are on the same button
            StartLevelSelectionSequence(level);
        }
    }

    private void StartLevelSelectionSequence(Level level)
    {
        var levelButtons = level.Tracker.GetEntities<LevelButton>().Cast<LevelButton>().ToList();

        // Find which buttons have players
        var buttonsWithPlayers = new List<LevelButton>();
        foreach (var button in levelButtons)
        {
            if (button.GetPlayersOnButton().Count > 0)
            {
                buttonsWithPlayers.Add(button);
            }
        }

        if (buttonsWithPlayers.Count == 0)
        {
            UmcLogger.Warn("No buttons with players found!");
            return;
        }

        // If all players are on the same button, go straight to StartLevel
        if (buttonsWithPlayers.Count == 1)
        {
            _winningButton = buttonsWithPlayers[0];
            _selectedMapSid = _winningButton.MapSID;
            UmcLogger.Info("All players on same button, starting level immediately");

            _countdownUI?.Hide();
            BroadcastVoteState(LevelVotePhase.Complete);

            StartLevel();
            return;
        }

        // Players are on different buttons - start elimination sequence
        UmcLogger.Info("Players on different buttons, starting elimination sequence");

        var session = GameSession.Instance;
        if (session == null) return;

        // Collect all players on buttons (for weighted random selection)
        var allPlayersOnButtons = new List<(UmcPlayer player, LevelButton button)>();
        foreach (var button in buttonsWithPlayers)
        {
            foreach (int playerSlot in button.GetPlayersOnButton())
            {
                var player = session.Players.GetAtSlot(playerSlot);
                if (player != null)
                {
                    allPlayersOnButtons.Add((player, button));
                }
            }
        }

        if (allPlayersOnButtons.Count == 0) return;

        // Pick a random PLAYER - this gives correct weighting
        // (if 3 players on A and 1 on B, 75% chance A wins)
        var (winningPlayer, winningButton) = allPlayersOnButtons[Calc.Random.Next(allPlayersOnButtons.Count)];
        _winningButton = winningButton;
        _selectedMapSid = _winningButton.MapSID;

        UmcLogger.Info($"Random player {winningPlayer.Name} selected, winning button: {_selectedMapSid}");

        // Find all players NOT on the winning button (losing players)
        _losingPlayers = new List<UmcPlayer>();
        foreach (var (player, button) in allPlayersOnButtons)
        {
            if (button != _winningButton)
            {
                _losingPlayers.Add(player);
            }
        }

        UmcLogger.Info($"{_losingPlayers.Count} losing players to eliminate");

        _eliminationActive = true;
        _eliminationTimer = ArrowEliminationInterval;

        _countdownUI?.Hide();
        BroadcastVoteState(LevelVotePhase.Eliminating);
    }

    private void UpdateArrowElimination(Level level)
    {
        _eliminationTimer -= Engine.DeltaTime;

        if (_eliminationTimer <= 0f)
        {
            _eliminationTimer = ArrowEliminationInterval;

            // Try to hide a random losing player's arrow
            if (!TryHideRandomLosingPlayer(level))
            {
                // No more losing players, start camera zoom
                UmcLogger.Info("All losing player arrows hidden, starting camera zoom");
                _eliminationActive = false;
                StartCameraZoom(level);
            }
        }
    }

    private bool TryHideRandomLosingPlayer(Level level)
    {
        if (_losingPlayers.Count == 0)
        {
            return false;
        }

        // Pick a random losing player
        int index = Calc.Random.Next(_losingPlayers.Count);
        var losingPlayer = _losingPlayers[index];
        _losingPlayers.RemoveAt(index);

        // Find which button this player is on and hide their arrow
        var levelButtons = level.Tracker.GetEntities<LevelButton>().Cast<LevelButton>().ToList();
        foreach (var button in levelButtons)
        {
            if (button.HasArrowForPlayer(losingPlayer.SlotIndex))
            {
                button.HideArrow(losingPlayer.SlotIndex);
                UmcLogger.Info($"Hiding arrow for player {losingPlayer.Name}");
                break;
            }
        }

        Audio.Play("event:/ui/main/button_toggle_off");

        BroadcastEliminate(losingPlayer.SlotIndex);

        return true;
    }

    private void StartCameraZoom(Level level)
    {
        _cameraZooming = true;
        _cameraZoomTimer = 0f;

        // Target the winning button using CameraController
        if (_winningButton != null)
        {
            // Center on the button with a nice zoom
            Vector2 buttonCenter = _winningButton.Position + new Vector2(32, 24); // Center of button
            CameraController.Instance?.SetFocusTarget(buttonCenter, 1.2f);
        }

        BroadcastVoteState(LevelVotePhase.CameraZoom);
    }

    private void UpdateCameraZoom(Level level)
    {
        _cameraZoomTimer += Engine.DeltaTime;

        if (_cameraZoomTimer >= CameraZoomDuration)
        {
            _cameraZooming = false;
            CameraController.Instance?.ClearFocusTarget();

            BroadcastVoteState(LevelVotePhase.Complete);

            if (IsHost)
            {
                StartLevel();
            }
        }
    }

    private void StartLevel()
    {
        var mapSid = ResolveMapSid(_selectedMapSid);
        if (mapSid == null)
        {
            UmcLogger.Error($"Cannot start level '{_selectedMapSid}' - no available map. Cancelling.");
            CancelLevelTransition();
            return;
        }

        UmcLogger.Info($"=== STARTING LEVEL: {mapSid} ===");
        NetworkManager.BroadcastWithSelf(new LoadLevelMessage { MapSid = mapSid });
    }

    /// <summary>
    /// Resolves the special "&lt;random&gt;" SID to a random available map, and
    /// validates that the chosen map actually exists.
    /// </summary>
    private string ResolveMapSid(string mapSid)
    {
        if (mapSid == LevelButton.RandomMapSid)
        {
            if (_lobby.Scene is not Level level) return null;

            var candidates = level.Tracker.GetEntities<LevelButton>()
                .Cast<LevelButton>()
                .Where(b => b.IsAvailable && !b.IsRandom)
                .Select(b => b.MapSID)
                .ToList();

            if (candidates.Count == 0) return null;

            var chosen = candidates[Calc.Random.Next(candidates.Count)];
            UmcLogger.Info($"Random level selected: {chosen}");
            return chosen;
        }

        return AreaData.Get(mapSid) != null ? mapSid : null;
    }

    /// <summary>
    /// Unfreezes the lobby if a level transition can't proceed.
    /// </summary>
    private void CancelLevelTransition()
    {
        if (PlayerSpawner.Instance != null)
            PlayerSpawner.Instance.InputsFrozen = false;
        if (LobbyPhase.Instance != null)
            LobbyPhase.Instance.IsLevelTransitioning = false;

        _selectedMapSid = null;
        _winningButton = null;
        _lastBroadcastNumber = -1;
        _countdownUI?.Hide();

        BroadcastVoteState(LevelVotePhase.None);
    }
}

/// <summary>
/// HUD element for the voting countdown display.
/// </summary>
public class LevelVotingCountdown : Entity
{
    private const float NumberScaleStart = 7.5f;
    private const float NumberScaleEnd = 4.1f;

    private bool _visible;
    private int _currentNumber;
    private int _previousNumber;
    private float _numberAnimProgress;
    private bool _showingGo;
    private float _goAnimProgress;
    private MTexture _circleTexture;

    public LevelVotingCountdown()
    {
        Tag = Tags.HUD | Tags.Global | Tags.PauseUpdate;
        Depth = -20000;
        _visible = false;

        // Load circle texture
        if (GFX.Gui.Has("umc/circle"))
        {
            _circleTexture = GFX.Gui["umc/circle"];
        }
    }

    public void Show(int startNumber)
    {
        _visible = true;
        _currentNumber = startNumber;
        _previousNumber = startNumber;
        _numberAnimProgress = 0f;
        _showingGo = false;
    }

    public void UpdateNumber(int number)
    {
        if (number != _currentNumber && number > 0)
        {
            _previousNumber = _currentNumber;
            _currentNumber = number;
            _numberAnimProgress = 0f;

            // Play tick sound
            Audio.Play("event:/ui/main/button_toggle_on");
        }
    }

    public void ShowGo()
    {
        _showingGo = true;
        _goAnimProgress = 0f;
    }

    public void Hide()
    {
        _visible = false;
        _showingGo = false;
    }

    public override void Update()
    {
        base.Update();

        if (!_visible) return;

        // Animate number scale
        _numberAnimProgress = Calc.Approach(_numberAnimProgress, 1f, Engine.DeltaTime * 3f);

        if (_showingGo)
        {
            _goAnimProgress = Calc.Approach(_goAnimProgress, 1f, Engine.DeltaTime * 2f);
        }
    }

    public override void Render()
    {
        base.Render();

        if (!_visible) return;

        Vector2 center = new Vector2(1920f / 2f, 1080f / 4f);

        // Draw circular transparent background
        DrawCircleBackground(center);

        // Always draw voting text
        DrawVotingText(center);

        if (_showingGo)
        {
            DrawGoText(center);
        }
        else
        {
            DrawCountdownNumber(center);
        }
    }

    private void DrawCircleBackground(Vector2 center)
    {
        if (_circleTexture != null)
        {
            // Draw the circle texture scaled down and more transparent
            float scale = 0.34f;
            _circleTexture.DrawCentered(center, Color.White * 0.5f, scale);
        }
    }

    private void DrawVotingText(Vector2 center)
    {
        string text = "Voting";
        float scale = 1.4f;
        Vector2 textSize = ActiveFont.Measure(text) * scale;

        // Position above center
        Vector2 textPos = center - new Vector2(textSize.X / 2f, 85f + textSize.Y / 2f);

        ActiveFont.DrawOutline(text, textPos, Vector2.Zero, Vector2.One * scale, Color.White, 2f, Color.Black);
    }

    private void DrawCountdownNumber(Vector2 center)
    {
        string text = _currentNumber.ToString();

        // Animate scale: starts big, shrinks to normal
        float scaleT = Ease.CubeOut(_numberAnimProgress);
        float scale = MathHelper.Lerp(NumberScaleStart, NumberScaleEnd, scaleT);

        // Also fade in slightly
        float alpha = MathHelper.Lerp(0.8f, 1f, scaleT);

        Vector2 textSize = ActiveFont.Measure(text) * scale;
        Vector2 textPos = center - new Vector2(textSize.X / 2f, textSize.Y / 2f - 50f);

        ActiveFont.DrawOutline(text, textPos, Vector2.Zero, Vector2.One * scale, Color.White * alpha, 3f, Color.Black);
    }

    private void DrawGoText(Vector2 center)
    {
        string text = "Go!";

        // Use same scale as number end scale, with pop animation
        float scaleT = Ease.BackOut(_goAnimProgress);
        float scale = NumberScaleEnd * 0.7f * scaleT;

        Vector2 textSize = ActiveFont.Measure(text) * scale;
        // Same position as the number
        Vector2 textPos = center - new Vector2(textSize.X / 2f, textSize.Y / 2f - 50f);

        ActiveFont.DrawOutline(text, textPos, Vector2.Zero, Vector2.One * scale, Color.LimeGreen, 3f, Color.Black);
    }
}

