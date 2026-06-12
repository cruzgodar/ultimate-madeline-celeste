using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Celeste.Mod.UltimateMadelineCeleste.Entities;
using Celeste.Mod.UltimateMadelineCeleste.Network;
using Celeste.Mod.UltimateMadelineCeleste.Network.Messages;
using Celeste.Mod.UltimateMadelineCeleste.Players;
using Celeste.Mod.UltimateMadelineCeleste.Props;
using Celeste.Mod.UltimateMadelineCeleste.Session;
using Celeste.Mod.UltimateMadelineCeleste.Utilities;
using Microsoft.Xna.Framework;
using Monocle;
using Steamworks;

namespace Celeste.Mod.UltimateMadelineCeleste.Phases.Playing;

public class PlacingPhase
{
    public event Action OnComplete;

    private readonly Dictionary<UmcPlayer, PropInstance> _playerProps = new();
    private readonly Dictionary<UmcPlayer, Vector2> _targetPositions = new();
    private readonly HashSet<UmcPlayer> _placedPlayers = new();
    private readonly HashSet<UmcPlayer> _needsSpawn = new();
    private readonly HashSet<UmcPlayer> _inSecondStage = new();
    private readonly Dictionary<UmcPlayer, Vector2> _firstStagePositions = new();
    private readonly Dictionary<UmcPlayer, PlayerInput> _playerInputs = new();
    private readonly Level _level;
    private PlayerCursors _cursors;
    private PlacingOverlay _overlay;

    private const float PositionLerpSpeed = 30f;

    private bool IsHost => NetworkManager.Instance?.IsHost ?? true;

    /// <summary>
    /// Calculates the top-left position for a prop so its bottom-left corner is at the cursor position.
    /// </summary>
    private static Vector2 GetTopLeftFromCursor(Vector2 cursorPos, PropInstance propInstance)
    {
        var size = propInstance.Prop.GetSize(propInstance.Rotation);
        return new Vector2(cursorPos.X, cursorPos.Y - size.Y);
    }

    public PlacingPhase(Level level, Dictionary<UmcPlayer, Prop> playerSelections)
    {
        _level = level;
        _cursors = new PlayerCursors(level, onConfirm: HandlePlacingConfirm);

        // Create the visual overlay (dark background + grid)
        _overlay = new PlacingOverlay();
        _overlay.SetPlacingProps(_playerProps);
        level.Add(_overlay);

        // Register network handlers
        NetworkManager.Handle<PropPlacedMessage>(HandlePropPlaced);
        NetworkManager.Handle<PropRotatedMessage>(HandlePropRotated);
        NetworkManager.Handle<PropFirstStageMessage>(HandlePropFirstStage);
        NetworkManager.Handle<PlacingCompleteMessage>(HandlePlacingComplete);
        NetworkManager.Handle<PropsDestroyedMessage>(HandlePropsDestroyed);

        // Setup props for each player
        foreach (var kvp in playerSelections)
        {
            var player = kvp.Key;
            var propInstance = new PropInstance(kvp.Value);
            _playerProps[player] = propInstance;
            _needsSpawn.Add(player);

            // Create input handler for local players
            if (player.IsLocal && player.Device != null)
            {
                _playerInputs[player] = new PlayerInput(player.Device);
            }
        }

        // Spawn cursors for local players
        _cursors.SpawnForLocalPlayers();
    }

    private void SpawnPreview(UmcPlayer player, PropInstance propInstance)
    {
        var cursorPos = _cursors.GetWorldPosition(player);

        // Snap to grid
        var snappedPos = new Vector2(
            (float)Math.Round(cursorPos.X / 8f) * 8f,
            (float)Math.Round(cursorPos.Y / 8f) * 8f
        );

        var topLeft = GetTopLeftFromCursor(snappedPos, propInstance);

        propInstance.Spawn(_level, topLeft);
        propInstance.SetPosition(topLeft);
        _targetPositions[player] = topLeft;

        // Pause the prop if it needs pausing during placing
        if (propInstance.Prop.PauseDuringPlacing && propInstance.Entity != null)
        {
            propInstance.Prop.SetPaused(propInstance.Entity, true);
        }

        UmcLogger.Info($"Created preview for {player.Name}: {propInstance.Prop.Name}");
    }

    public void Update()
    {
        // Spawn previews for players that need them (wait for cursor to exist)
        foreach (var player in _needsSpawn.ToList())
        {
            if (!_cursors.Has(player)) continue;

            if (_playerProps.TryGetValue(player, out var propInstance))
            {
                SpawnPreview(player, propInstance);
                _needsSpawn.Remove(player);

                // Set initial help text for two-stage props
                if (propInstance.IsTwoStage)
                {
                    var cursor = _cursors.GetFor(player);
                    if (cursor != null)
                        cursor.HelpText = propInstance.Prop.FirstStageLabel;
                }
            }
        }

        foreach (var kvp in _playerProps)
        {
            var player = kvp.Key;
            var propInstance = kvp.Value;

            if (_placedPlayers.Contains(player)) continue;
            if (!propInstance.IsSpawned) continue;
            if (!_cursors.Has(player)) continue;

            // Handle rotation input for local players
            if (_playerInputs.TryGetValue(player, out var input))
            {
                if (input.RotateRight.Pressed)
                {
                    input.RotateRight.ConsumePress();
                    RotateProp(player, propInstance, 1);
                }
                else if (input.RotateLeft.Pressed)
                {
                    input.RotateLeft.ConsumePress();
                    RotateProp(player, propInstance, -1);
                }
            }

            // Calculate the snapped cursor position
            var worldPos = _cursors.GetWorldPosition(player);
            var snappedPos = new Vector2(
                (float)Math.Round(worldPos.X / 8f) * 8f,
                (float)Math.Round(worldPos.Y / 8f) * 8f
            );

            if (_inSecondStage.Contains(player))
            {
                // Second stage: position is locked, update target
                var targetTopLeft = GetTopLeftFromCursor(snappedPos, propInstance);
                propInstance.SetTarget(targetTopLeft);
            }
            else
            {
                // First stage: update position normally
                var targetTopLeft = GetTopLeftFromCursor(snappedPos, propInstance);
                _targetPositions[player] = targetTopLeft;

                // Smoothly interpolate to the target position
                var currentPos = propInstance.Position;
                var t = 1f - (float)Math.Exp(-PositionLerpSpeed * Engine.DeltaTime);
                var smoothedPos = Vector2.Lerp(currentPos, targetTopLeft, t);

                propInstance.SetPosition(smoothedPos);
            }
        }
    }

    private void RotateProp(UmcPlayer player, PropInstance propInstance, int direction)
    {
        // Only rotate if the prop supports rotation
        if (propInstance.Prop.AllowedRotation == RotationMode.None)
            return;

        // Calculate new rotation
        float currentRotation = propInstance.Rotation;
        float step = propInstance.Prop.AllowedRotation == RotationMode.Rotate90 ? 90f : 180f;
        float newRotation = (currentRotation + direction * step + 360f) % 360f;

        propInstance.SetRotation(newRotation);
        Audio.Play("event:/ui/main/button_toggle_on");
        UmcLogger.Info($"Player {player.Name} rotated {propInstance.Prop.Name} to {newRotation}°");

        // Send rotation update over network
        if (NetworkManager.Instance?.IsOnline == true)
        {
            NetworkManager.Broadcast(new PropRotatedMessage
            {
                PlayerIndex = player.SlotIndex,
                Rotation = newRotation
            });
        }
    }

    private void HandlePlacingConfirm(UmcPlayer player, Vector2 position)
    {
        if (_placedPlayers.Contains(player))
        {
            UmcLogger.Info($"Player {player.Name} already placed their prop");
            return;
        }

        if (!_playerProps.TryGetValue(player, out var propInstance))
        {
            UmcLogger.Warn($"Player {player.Name} has no selected prop to place");
            return;
        }

        var snappedPos = new Vector2(
            (float)Math.Round(position.X / 8f) * 8f,
            (float)Math.Round(position.Y / 8f) * 8f
        );

        // Check if placement is blocked
        var topLeft = GetTopLeftFromCursor(snappedPos, propInstance);
        var propSize = propInstance.Prop.GetSize(propInstance.Rotation);
        if (PlacementBlocker.IsBlocked(_level, topLeft, (int)propSize.X, (int)propSize.Y))
        {
            // Play error sound and reject placement
            Audio.Play("event:/ui/main/button_invalid");
            UmcLogger.Info($"Player {player.Name} tried to place in blocked area");
            return;
        }

        // Handle two-stage placement
        if (propInstance.IsTwoStage && !_inSecondStage.Contains(player))
        {
            // First stage: lock start position, move to second stage
            propInstance.SetPosition(topLeft);
            _firstStagePositions[player] = topLeft;
            _inSecondStage.Add(player);

            // Update cursor help text to second stage label
            var cursor = _cursors.GetFor(player);
            if (cursor != null)
                cursor.HelpText = propInstance.Prop.SecondStageLabel;

            Audio.Play("event:/ui/main/button_toggle_on");
            UmcLogger.Info($"Player {player.Name} set start position for {propInstance.Prop.Name}, now selecting target");

            // Broadcast first stage over network
            if (NetworkManager.Instance?.IsOnline == true)
            {
                NetworkManager.Broadcast(new PropFirstStageMessage
                {
                    PlayerIndex = player.SlotIndex,
                    PositionX = topLeft.X,
                    PositionY = topLeft.Y
                });
            }

            return;
        }

        // If online, send to host for validation; otherwise process locally
        if (NetworkManager.Instance?.IsOnline == true && !IsHost)
        {
            var msg = new PropPlacedMessage
            {
                PlayerIndex = player.SlotIndex,
                PositionX = snappedPos.X,
                PositionY = snappedPos.Y,
                Rotation = propInstance.Rotation
            };

            // Include first stage position for two-stage props
            if (_firstStagePositions.TryGetValue(player, out var firstPos))
            {
                msg.HasFirstStage = true;
                msg.FirstStageX = firstPos.X;
                msg.FirstStageY = firstPos.Y;
            }

            NetworkManager.SendToHost(msg);
        }
        else
        {
            // Host or local - process and broadcast
            Vector2? firstStagePos = _firstStagePositions.TryGetValue(player, out var fp) ? fp : null;
            ProcessPropPlacement(player, snappedPos, propInstance, firstStagePos);

            if (NetworkManager.Instance?.IsOnline == true)
            {
                var msg = new PropPlacedMessage
                {
                    PlayerIndex = player.SlotIndex,
                    PositionX = snappedPos.X,
                    PositionY = snappedPos.Y,
                    Rotation = propInstance.Rotation
                };

                if (firstStagePos.HasValue)
                {
                    msg.HasFirstStage = true;
                    msg.FirstStageX = firstStagePos.Value.X;
                    msg.FirstStageY = firstStagePos.Value.Y;
                }

                NetworkManager.Broadcast(msg);
            }
        }
    }

    private void ProcessPropPlacement(UmcPlayer player, Vector2 snappedPos, PropInstance propInstance, Vector2? firstStagePos = null)
    {
        if (_placedPlayers.Contains(player)) return;

        var targetTopLeft = GetTopLeftFromCursor(snappedPos, propInstance);

        if (firstStagePos.HasValue)
        {
            // Two-stage prop: first stage is the position, second stage is the target
            propInstance.SetPosition(firstStagePos.Value);
            propInstance.SetTarget(targetTopLeft);
            UmcLogger.Info($"Player {player.Name} placed {propInstance.Prop.Name} at {firstStagePos.Value} with target {targetTopLeft}");
        }
        else
        {
            // Single-stage prop
            propInstance.SetPosition(targetTopLeft);
            UmcLogger.Info($"Player {player.Name} placed {propInstance.Prop.Name} at {targetTopLeft}");
        }

        // Notify the prop it was placed (for visual effects like bomb flash)
        propInstance.Prop.OnPlaced(propInstance.Entity);

        // Handle bomb explosion - host calculates and broadcasts destruction
        if (IsHost && propInstance.Prop is BombProp bombProp && propInstance.Entity is PlacedBomb bomb)
        {
            // Host calculates which props to destroy
            var destroyedIndices = BombProp.CalculateDestroyedPropIndices(bomb.Position, bomb.Size);

            if (destroyedIndices.Count > 0)
            {
                // Broadcast destruction to all clients (including self)
                NetworkManager.BroadcastWithSelf(new PropsDestroyedMessage
                {
                    PropIndices = destroyedIndices
                });
            }
        }

        // Register prop ownership for trap kill tracking (unless prop skips registration)
        if (!propInstance.Prop.SkipRegistration)
        {
            RoundState.Current?.RegisterPlacedProp(propInstance, player);
        }

        _placedPlayers.Add(player);
        _inSecondStage.Remove(player);
        _firstStagePositions.Remove(player);
        Audio.Play("event:/ui/main/button_select");

        _cursors.Remove(player);
        CheckAllPlayersPlaced();
    }

    private void CheckAllPlayersPlaced()
    {
        var session = GameSession.Instance;
        if (session == null) return;

        int totalPlayers = session.Players.All.Count;
        int placedCount = _placedPlayers.Count;

        UmcLogger.Info($"Placed: {placedCount}/{totalPlayers}");

        if (placedCount >= totalPlayers)
        {
            // Host triggers completion
            if (IsHost)
            {
                NetworkManager.BroadcastWithSelf(new PlacingCompleteMessage());
            }
        }
    }

    #region Network Handlers

    private void HandlePropPlaced(CSteamID sender, PropPlacedMessage message)
    {
        var session = GameSession.Instance;
        if (session == null) return;

        var player = session.Players.GetAtSlot(message.PlayerIndex);
        if (player == null) return;

        if (!_playerProps.TryGetValue(player, out var propInstance)) return;

        var snappedPos = new Vector2(message.PositionX, message.PositionY);
        Vector2? firstStagePos = message.HasFirstStage
            ? new Vector2(message.FirstStageX, message.FirstStageY)
            : null;

        // Apply rotation from message
        propInstance.SetRotation(message.Rotation);

        // If we're host and received from client, validate and rebroadcast
        if (IsHost && sender.m_SteamID != NetworkManager.Instance.LocalClientId)
        {
            ProcessPropPlacement(player, snappedPos, propInstance, firstStagePos);
            NetworkManager.Broadcast(new PropPlacedMessage
            {
                PlayerIndex = message.PlayerIndex,
                PositionX = message.PositionX,
                PositionY = message.PositionY,
                Rotation = message.Rotation,
                HasFirstStage = message.HasFirstStage,
                FirstStageX = message.FirstStageX,
                FirstStageY = message.FirstStageY
            });
        }
        else if (!IsHost)
        {
            // Client receiving from host
            ProcessPropPlacement(player, snappedPos, propInstance, firstStagePos);
        }
    }

    private void HandlePropRotated(CSteamID sender, PropRotatedMessage message)
    {
        var session = GameSession.Instance;
        if (session == null) return;

        var player = session.Players.GetAtSlot(message.PlayerIndex);
        if (player == null) return;

        // Don't apply to local players (they already rotated locally)
        if (player.IsLocal) return;

        if (!_playerProps.TryGetValue(player, out var propInstance)) return;

        propInstance.SetRotation(message.Rotation);
        UmcLogger.Info($"Remote player {player.Name} rotated to {message.Rotation}°");
    }

    private void HandlePropFirstStage(CSteamID sender, PropFirstStageMessage message)
    {
        var session = GameSession.Instance;
        if (session == null) return;

        var player = session.Players.GetAtSlot(message.PlayerIndex);
        if (player == null) return;

        // Don't apply to local players (they already handled it locally)
        if (player.IsLocal) return;

        if (!_playerProps.TryGetValue(player, out var propInstance)) return;

        var position = new Vector2(message.PositionX, message.PositionY);
        propInstance.SetPosition(position);
        _firstStagePositions[player] = position;
        _inSecondStage.Add(player);

        var cursor = _cursors.GetFor(player);
        cursor?.HelpText = propInstance.Prop.SecondStageLabel;

        UmcLogger.Info($"Remote player {player.Name} set first stage position at {position}");
    }

    private void HandlePlacingComplete(PlacingCompleteMessage message)
    {
        // Fade out the overlay before completing
        _overlay?.FadeOut();
        OnComplete?.Invoke();
    }

    private void HandlePropsDestroyed(PropsDestroyedMessage message)
    {
        var roundState = RoundState.Current;
        if (roundState == null) return;

        // Destroy props by index (in reverse order to maintain correct indices)
        var sortedIndices = message.PropIndices.OrderByDescending(i => i).ToList();

        foreach (var index in sortedIndices)
        {
            if (index < 0 || index >= roundState.PlacedProps.Count) continue;

            var placedProp = roundState.PlacedProps[index];
            UmcLogger.Info($"Bomb destroyed: {placedProp.Prop.Prop.Name} placed by {placedProp.Owner?.Name}");
            placedProp.Prop.Despawn();
            roundState.PlacedProps.RemoveAt(index);
        }

        if (message.PropIndices.Count > 0)
        {
            UmcLogger.Info($"Bomb explosion destroyed {message.PropIndices.Count} prop(s)");
        }
    }

    #endregion
}

#region Messages

public class PropPlacedMessage : INetMessage
{
    public int PlayerIndex { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }
    public float Rotation { get; set; }

    // For two-stage placement
    public bool HasFirstStage { get; set; }
    public float FirstStageX { get; set; }
    public float FirstStageY { get; set; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)PlayerIndex);
        writer.Write(PositionX);
        writer.Write(PositionY);
        writer.Write(Rotation);
        writer.Write(HasFirstStage);
        if (HasFirstStage)
        {
            writer.Write(FirstStageX);
            writer.Write(FirstStageY);
        }
    }

    public void Deserialize(BinaryReader reader)
    {
        PlayerIndex = reader.ReadByte();
        PositionX = reader.ReadSingle();
        PositionY = reader.ReadSingle();
        Rotation = reader.ReadSingle();
        HasFirstStage = reader.ReadBoolean();
        if (HasFirstStage)
        {
            FirstStageX = reader.ReadSingle();
            FirstStageY = reader.ReadSingle();
        }
    }
}

public class PropRotatedMessage : INetMessage
{
    public int PlayerIndex { get; set; }
    public float Rotation { get; set; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)PlayerIndex);
        writer.Write(Rotation);
    }

    public void Deserialize(BinaryReader reader)
    {
        PlayerIndex = reader.ReadByte();
        Rotation = reader.ReadSingle();
    }
}

public class PropFirstStageMessage : INetMessage
{
    public int PlayerIndex { get; set; }
    public float PositionX { get; set; }
    public float PositionY { get; set; }

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)PlayerIndex);
        writer.Write(PositionX);
        writer.Write(PositionY);
    }

    public void Deserialize(BinaryReader reader)
    {
        PlayerIndex = reader.ReadByte();
        PositionX = reader.ReadSingle();
        PositionY = reader.ReadSingle();
    }
}

public class PlacingCompleteMessage : INetMessage
{
    public void Serialize(BinaryWriter writer) { }
    public void Deserialize(BinaryReader reader) { }
}

public class PropsDestroyedMessage : INetMessage
{
    public List<int> PropIndices { get; set; } = new();

    public void Serialize(BinaryWriter writer)
    {
        writer.Write((byte)PropIndices.Count);
        foreach (var index in PropIndices)
        {
            writer.Write((ushort)index);
        }
    }

    public void Deserialize(BinaryReader reader)
    {
        int count = reader.ReadByte();
        PropIndices = new List<int>(count);
        for (int i = 0; i < count; i++)
        {
            PropIndices.Add(reader.ReadUInt16());
        }
    }
}

#endregion
