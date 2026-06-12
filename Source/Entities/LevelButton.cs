using System;
using System.Collections.Generic;
using Celeste.Mod.Entities;
using Celeste.Mod.UltimateMadelineCeleste.Players;
using Celeste.Mod.UltimateMadelineCeleste.UI.Lobby;
using Celeste.Mod.UltimateMadelineCeleste.Utilities;
using Microsoft.Xna.Framework;
using Monocle;

namespace Celeste.Mod.UltimateMadelineCeleste.Entities;

/// <summary>
/// A level selection button (like Ultimate Chicken Horse).
/// Features a pressable button at the bottom and a level preview above.
/// </summary>
[CustomEntity("UltimateMadelineCeleste/LevelButton")]
[Tracked]
public class LevelButton : Entity
{
    // Entity dimensions (in pixels) - 8x6 tiles (aligned to 8px grid)
    private const int EntityWidth = 64;
    private const int EntityHeight = 48;

    /// <summary>
    /// Special MapSID value meaning "pick a random available level".
    /// </summary>
    public const string RandomMapSid = "<random>";

    // Button dimensions
    private const int ButtonWidth = 64;
    private const int ButtonBaseHeight = 5;
    private const float ButtonUnpressedOffset = 3f; // Orange peeks out 3 pixels when not pressed
    private const float ButtonPressedOffset = 1f;   // Orange shows 1 pixel when pressed
    private const float ButtonAnimationSpeed = 12f; // Animation speed

    /// <summary>
    /// The map SID this button leads to.
    /// </summary>
    public string MapSID { get; private set; }

    /// <summary>
    /// Path to the preview texture (relative to Gameplay atlas).
    /// </summary>
    public string PreviewTexturePath { get; private set; }

    /// <summary>
    /// The text to display on the button.
    /// </summary>
    public string DisplayText { get; private set; }

    /// <summary>
    /// Whether this button picks a random level.
    /// </summary>
    public bool IsRandom => MapSID == RandomMapSid;

    /// <summary>
    /// Whether this button can be voted on. False when the target map doesn't exist
    /// (e.g. a level that hasn't been made yet).
    /// </summary>
    public bool IsAvailable { get; private set; }

    /// <summary>
    /// The color of the text.
    /// </summary>
    public Color TextColor { get; private set; }

    /// <summary>
    /// The preview texture displayed above the button.
    /// </summary>
    private MTexture _previewTexture;

    /// <summary>
    /// The gray button base texture.
    /// </summary>
    private MTexture _buttonBaseTexture;

    /// <summary>
    /// The orange pressable button texture.
    /// </summary>
    private MTexture _buttonOrangeTexture;

    /// <summary>
    /// Current button press offset (animated between 3 and 1).
    /// </summary>
    private float _currentButtonOffset;

    /// <summary>
    /// Whether a player is currently standing on the button.
    /// </summary>
    private bool _isPressed;

    /// <summary>
    /// The JumpThru platform that players can stand on.
    /// </summary>
    private ButtonPlatform _platform;

    /// <summary>
    /// The solid base that blocks players from the sides.
    /// </summary>
    private Solid _baseSolid;

    // Arrow system
    private MTexture[] _arrowTextures; // 3 textures at different angles
    private readonly Dictionary<int, ArrowState> _playerArrows = new(); // slot index -> arrow state
    private const float ArrowAnimationSpeed = 8f;

    // Arrow positions around the button (offset from center, pointing direction)
    // 0 deg = lowest, mid angle = 3/4 up, 45 deg = top
    private static readonly ArrowSlot[] ArrowSlots =
    [
        new(-36, 8, false, 0),    // Left side, low (0 deg)
        new(36, 8, true, 0),     // Right side, low (0 deg)
        new(-40, -10, false, 1),  // Left side, mid-high (mid angle)
        new(40, -10, true, 1),   // Right side, mid-high (mid angle)
        new(-38, -26, false, 2),  // Left side, top (45 deg)
        new(38, -26, true, 2),   // Right side, top (45 deg)
    ];

    public LevelButton(EntityData data, Vector2 offset) : base(data.Position + offset)
    {
        MapSID = data.Attr("mapSID", "");
        PreviewTexturePath = data.Attr("previewTexture", "");
        DisplayText = data.Attr("text", "");

        // Parse text color (hex format, default to white)
        string colorHex = data.Attr("textColor", "FFFFFF");
        try
        {
            TextColor = Calc.HexToColor(colorHex);
        }
        catch
        {
            TextColor = Color.White;
        }

        // Initialize button state (unpressed)
        _currentButtonOffset = ButtonUnpressedOffset;
        _isPressed = false;

        Depth = Depths.Below;
    }

    public override void Added(Scene scene)
    {
        base.Added(scene);

        LoadTextures();

        // Create the platform that players stand on
        // Position it at the top of the button (where orange part is)
        float platformY = Position.Y + EntityHeight - ButtonBaseHeight - ButtonUnpressedOffset;
        _platform = new ButtonPlatform(new Vector2(Position.X, platformY), ButtonWidth, this);
        scene.Add(_platform);

        // Create the solid base that blocks players from the sides
        Vector2 basePos = new Vector2(Position.X, Position.Y + EntityHeight - ButtonBaseHeight);
        _baseSolid = new Solid(basePos, ButtonWidth, ButtonBaseHeight, safe: false);
        _baseSolid.Collidable = true;
        scene.Add(_baseSolid);

        IsAvailable = IsRandom || (!string.IsNullOrEmpty(MapSID) && AreaData.Get(MapSID) != null);
        if (!IsAvailable)
            UmcLogger.Warn($"LevelButton: Map '{MapSID}' not found - button disabled");

        UmcLogger.Info($"LevelButton added: MapSID='{MapSID}', Preview='{PreviewTexturePath}', Available={IsAvailable}");
    }

    public override void Removed(Scene scene)
    {
        base.Removed(scene);

        // Remove the platform and solid when this entity is removed
        _platform?.RemoveSelf();
        _baseSolid?.RemoveSelf();
    }

    /// <summary>
    /// Loads all required textures.
    /// </summary>
    private void LoadTextures()
    {
        // Load preview texture if specified
        if (!string.IsNullOrEmpty(PreviewTexturePath))
        {
            if (GFX.Game.Has(PreviewTexturePath))
            {
                _previewTexture = GFX.Game[PreviewTexturePath];
            }
            else
            {
                UmcLogger.Warn($"LevelButton: Preview texture not found: {PreviewTexturePath}");
            }
        }

        // Load button textures
        const string basePath = "objects/UMC/levelButton/";

        if (GFX.Game.Has(basePath + "buttonBase"))
        {
            _buttonBaseTexture = GFX.Game[basePath + "buttonBase"];
        }
        else
        {
            UmcLogger.Warn($"LevelButton: Button base texture not found: {basePath}buttonBase");
        }

        if (GFX.Game.Has(basePath + "buttonOrange"))
        {
            _buttonOrangeTexture = GFX.Game[basePath + "buttonOrange"];
        }
        else
        {
            UmcLogger.Warn($"LevelButton: Button orange texture not found: {basePath}buttonOrange");
        }

        // Load arrow textures
        _arrowTextures = new MTexture[3];
        string[] arrowNames = ["arrow_0", "arrow_1", "arrow_2"];
        for (int i = 0; i < 3; i++)
        {
            if (GFX.Game.Has(basePath + arrowNames[i]))
            {
                _arrowTextures[i] = GFX.Game[basePath + arrowNames[i]];
            }
            else
            {
                UmcLogger.Warn($"LevelButton: Arrow texture not found: {basePath}{arrowNames[i]}");
            }
        }
    }

    public override void Update()
    {
        base.Update();

        // Get which players are currently on the button
        var playersOnButton = GetPlayersOnButton();
        _isPressed = playersOnButton.Count > 0;

        // Update arrow states for players
        UpdateArrows(playersOnButton);

        // Animate button press
        float targetOffset = _isPressed ? ButtonPressedOffset : ButtonUnpressedOffset;
        float previousOffset = _currentButtonOffset;
        _currentButtonOffset = Calc.Approach(_currentButtonOffset, targetOffset, ButtonAnimationSpeed * Engine.DeltaTime);

        // Move the platform with the button animation
        if (_platform != null && previousOffset != _currentButtonOffset)
        {
            float deltaY = previousOffset - _currentButtonOffset; // Positive when pressing down
            _platform.MoveV(deltaY);
        }
    }

    /// <summary>
    /// Updates arrow animations for all players.
    /// </summary>
    private void UpdateArrows(HashSet<int> playersOnButton)
    {
        // Add arrows for new players on button
        foreach (int slotIndex in playersOnButton)
        {
            if (!_playerArrows.ContainsKey(slotIndex))
            {
                // Pick a random arrow slot each time
                int arrowSlotIndex = Calc.Random.Next(ArrowSlots.Length);
                _playerArrows[slotIndex] = new ArrowState
                {
                    SlotIndex = arrowSlotIndex,
                    Scale = 0f,
                    SlideProgress = 0f,
                    IsVisible = true,
                    ForceHidden = false
                };
            }
            else if (!_playerArrows[slotIndex].ForceHidden)
            {
                // Only set visible if not force hidden
                _playerArrows[slotIndex].IsVisible = true;
            }
        }

        // Update all arrow animations
        var toRemove = new List<int>();
        foreach (var kvp in _playerArrows)
        {
            var arrow = kvp.Value;

            if (arrow.IsVisible)
            {
                // Slide in from behind the background
                arrow.SlideProgress = Calc.Approach(arrow.SlideProgress, 1f, ArrowAnimationSpeed * 0.6f * Engine.DeltaTime);

                // Scale with overshoot when appearing
                if (arrow.Scale < 1f)
                {
                    arrow.Scale = Calc.Approach(arrow.Scale, 1.15f, ArrowAnimationSpeed * Engine.DeltaTime);
                    if (arrow.Scale >= 1.15f) arrow.Scale = 1f;
                }
            }
            else
            {
                // Slide back behind and shrink
                arrow.SlideProgress = Calc.Approach(arrow.SlideProgress, 0f, ArrowAnimationSpeed * 0.8f * Engine.DeltaTime);
                arrow.Scale = Calc.Approach(arrow.Scale, 0f, ArrowAnimationSpeed * Engine.DeltaTime);
            }

            // Mark for removal when fully hidden (but not if force hidden - keep the entry to prevent re-creation)
            if (!arrow.IsVisible && arrow.Scale <= 0f && arrow.SlideProgress <= 0f && !arrow.ForceHidden)
            {
                toRemove.Add(kvp.Key);
            }

            // Mark as not visible for next frame (will be set true again if still on button)
            arrow.IsVisible = false;
        }

        // Remove fully hidden arrows
        foreach (int slot in toRemove)
        {
            _playerArrows.Remove(slot);
        }
    }

    /// <summary>
    /// Checks if this button has a visible arrow for the given player slot.
    /// </summary>
    public bool HasArrowForPlayer(int playerSlot)
    {
        return _playerArrows.ContainsKey(playerSlot) && _playerArrows[playerSlot].Scale > 0.01f;
    }

    /// <summary>
    /// Hides the arrow for the given player slot (animates it out).
    /// </summary>
    public void HideArrow(int playerSlot)
    {
        if (_playerArrows.TryGetValue(playerSlot, out var arrow))
        {
            arrow.IsVisible = false;
            arrow.ForceHidden = true; // Prevent it from being shown again
        }
    }

    /// <summary>
    /// Gets the slot indices of all players standing on the button.
    /// </summary>
    public HashSet<int> GetPlayersOnButton()
    {
        var result = new HashSet<int>();
        if (!IsAvailable) return result;
        if (Scene is not Level level) return result;

        // Check area on top of the button
        var checkRect = new Rectangle(
            (int)Position.X,
            (int)(Position.Y + EntityHeight - ButtonBaseHeight - 8), // Above button surface
            ButtonWidth,
            8
        );

        // Check local players via the platform
        if (_platform != null && _platform.HasPlayerRider())
        {
            // Find which local player is riding
            var spawner = PlayerSpawner.Instance;
            if (spawner != null)
            {
                foreach (var player in level.Tracker.GetEntities<Player>())
                {
                    if (player is Player p && p.CollideRect(checkRect))
                    {
                        var umcPlayer = spawner.GetUmcPlayer(p);
                        if (umcPlayer != null)
                        {
                            result.Add(umcPlayer.SlotIndex);
                        }
                    }
                }
            }
        }

        // Check remote players
        foreach (var entity in level.Tracker.GetEntities<RemotePlayer>())
        {
            if (entity is RemotePlayer remote && !remote.Dead)
            {
                if (remote.CollideRect(checkRect))
                {
                    result.Add(remote.UmcPlayer.SlotIndex);
                }
            }
        }

        return result;
    }

    public override void Render()
    {
        base.Render();

        // Calculate positions
        Vector2 basePos = Position;

        // Preview area dimensions (full width - 2px, centered, resting on gray base)
        int orangeWidth = ButtonWidth - 8; // 56px
        int previewWidth = ButtonWidth - 2; // 62px
        int previewHeight = EntityHeight - ButtonBaseHeight; // Height up to the gray base
        int previewX = (EntityWidth - previewWidth) / 2; // Center horizontally (1px each side)

        // 1. Draw preview texture (background, above the button)
        if (_previewTexture != null)
        {
            // Center the preview in the preview area
            Vector2 previewPos = basePos + new Vector2(EntityWidth / 2f, previewHeight / 2f);
            _previewTexture.DrawCentered(previewPos);
        }
        else
        {
            // Draw placeholder rectangle if no preview texture
            Draw.Rect(basePos.X + previewX, basePos.Y, previewWidth, previewHeight, Color.DarkSlateGray);
        }

        // 2. Draw orange button part (animated)
        // Position at bottom, offset upward based on current animation state
        int orangeX = (ButtonWidth - orangeWidth) / 2; // Center it
        float orangeY = EntityHeight - ButtonBaseHeight - _currentButtonOffset;

        if (_buttonOrangeTexture != null)
        {
            // Center the texture
            Vector2 orangePos = basePos + new Vector2(EntityWidth / 2f, orangeY + 2);
            _buttonOrangeTexture.DrawCentered(orangePos);
        }
        else
        {
            // Fallback: draw centered orange rectangle
            Draw.Rect(basePos.X + orangeX, basePos.Y + orangeY, orangeWidth, 4, Color.Orange);
        }

        // 3. Draw gray button base (bottom, covers bottom of orange when pressed)
        Vector2 baseButtonPos = basePos + new Vector2(0, EntityHeight - ButtonBaseHeight);
        if (_buttonBaseTexture != null)
        {
            _buttonBaseTexture.Draw(baseButtonPos);
        }
        else
        {
            // Fallback: draw gray rectangle
            Draw.Rect(baseButtonPos.X, baseButtonPos.Y, ButtonWidth, ButtonBaseHeight, Color.Gray);
        }

        // 4. Draw text (if specified)
        if (!string.IsNullOrEmpty(DisplayText))
        {
            RenderText(basePos, previewWidth, previewHeight);
        }

        // 5. Darken unavailable buttons (map doesn't exist yet)
        if (!IsAvailable)
        {
            Draw.Rect(basePos.X + previewX, basePos.Y, previewWidth, previewHeight, Color.Black * 0.65f);
        }

        // 6. Draw player arrows
        RenderArrows(basePos);
    }

    /// <summary>
    /// Renders the display text with newline support and shrink-to-fit using the pixel font.
    /// </summary>
    private void RenderText(Vector2 basePos, int availableWidth, int availableHeight)
    {
        if (string.IsNullOrEmpty(DisplayText)) return;

        // Get the pixel font from the English dialog (same as speedrun timer, etc.)
        var language = Dialog.Languages["english"];
        var font = language.Font;
        var fontSize = language.FontFaceSize;
        var fontSizeData = font.Get(fontSize);
        if (fontSizeData == null) return;

        // Split text by newlines
        string[] lines = DisplayText.Split('\n');
        if (lines.Length == 0) return;

        // Calculate available area (with padding)
        float padding = 2f;
        float maxWidth = availableWidth - padding * 2;
        float maxHeight = availableHeight - padding * 2;

        // Measure all lines to determine the scale needed
        float maxLineWidth = 0f;
        float totalHeight = 0f;
        const float lineSpacing = 1.2f; // Line spacing multiplier

        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line)) continue;
            Vector2 lineSize = fontSizeData.Measure(line);
            maxLineWidth = Math.Max(maxLineWidth, lineSize.X);
            totalHeight += lineSize.Y * lineSpacing;
        }

        // Calculate scale to fit both width and height
        float scaleX = maxLineWidth > 0 ? Math.Min(1f, maxWidth / maxLineWidth) : 1f;
        float scaleY = totalHeight > 0 ? Math.Min(1f, maxHeight / totalHeight) : 1f;
        float scale = Math.Min(scaleX, scaleY);

        // Recalculate total height with the actual scale
        totalHeight = 0f;
        foreach (string line in lines)
        {
            float lineHeight = string.IsNullOrEmpty(line)
                ? fontSizeData.Measure(" ").Y * scale
                : fontSizeData.Measure(line).Y * scale;
            totalHeight += lineHeight * lineSpacing;
        }

        // Calculate starting position (top-aligned with padding)
        float startX = basePos.X + EntityWidth / 2f;
        float startY = basePos.Y + padding;

        // Render each line
        float currentY = startY;
        foreach (string line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                // Empty line - just advance Y position
                float emptyHeight = fontSizeData.Measure(" ").Y * scale;
                currentY += emptyHeight * lineSpacing;
                continue;
            }

            Vector2 lineSize = fontSizeData.Measure(line) * scale;
            Vector2 linePos = new Vector2(startX, currentY);

            // Draw using pixel font
            font.Draw(
                fontSize,
                line,
                linePos,
                new Vector2(0.5f, 0f), // Center horizontally, top-aligned vertically
                Vector2.One * scale,
                TextColor
            );

            currentY += lineSize.Y * lineSpacing;
        }
    }

    /// <summary>
    /// Renders all player arrows around the button.
    /// </summary>
    private void RenderArrows(Vector2 basePos)
    {
        Vector2 buttonCenter = basePos + new Vector2(EntityWidth / 2f, EntityHeight / 2f);

        foreach (var kvp in _playerArrows)
        {
            int playerSlot = kvp.Key;
            var arrow = kvp.Value;

            if (arrow.Scale <= 0.01f) continue;

            var slot = ArrowSlots[arrow.SlotIndex % ArrowSlots.Length];
            var texture = _arrowTextures?[slot.TextureIndex];

            // Get player color
            Color arrowColor = playerSlot < LobbyUi.PlayerColors.Length
                ? LobbyUi.PlayerColors[playerSlot]
                : Color.White;

            // Interpolate position from behind the preview (center) to final position
            float slideEased = Ease.CubeOut(arrow.SlideProgress);
            Vector2 arrowPos = buttonCenter + new Vector2(
                slot.OffsetX * slideEased,
                slot.OffsetY * slideEased
            );

            if (texture != null)
            {
                // Flip horizontally if on right side
                Vector2 scale = new Vector2(slot.FlipX ? -arrow.Scale : arrow.Scale, arrow.Scale);
                texture.DrawCentered(arrowPos, arrowColor, scale);
            }
            else
            {
                // Fallback: draw a simple triangle
                float size = 8f * arrow.Scale;
                float dir = slot.FlipX ? -1f : 1f;
                Vector2 tip = arrowPos + new Vector2(dir * size, 0);
                Vector2 corner1 = arrowPos + new Vector2(-dir * size * 0.5f, -size * 0.6f);
                Vector2 corner2 = arrowPos + new Vector2(-dir * size * 0.5f, size * 0.6f);
                Draw.Line(tip, corner1, arrowColor, 2f);
                Draw.Line(tip, corner2, arrowColor, 2f);
                Draw.Line(corner1, corner2, arrowColor, 2f);
            }
        }
    }
}

/// <summary>
/// State for a player's arrow indicator.
/// </summary>
public class ArrowState
{
    public int SlotIndex { get; set; }
    public float Scale { get; set; }
    public float SlideProgress { get; set; } // 0 = behind preview, 1 = at final position
    public bool IsVisible { get; set; }
    public bool ForceHidden { get; set; } // When true, arrow stays hidden regardless of player position
}

/// <summary>
/// Defines a position slot for an arrow around the button.
/// </summary>
public readonly struct ArrowSlot(float offsetX, float offsetY, bool flipX, int textureIndex)
{
    public readonly float OffsetX = offsetX;
    public readonly float OffsetY = offsetY;
    public readonly bool FlipX = flipX;
    public readonly int TextureIndex = textureIndex;
}

/// <summary>
/// A JumpThru platform for the level button that players can stand on.
/// </summary>
public class ButtonPlatform : JumpThru
{
    private readonly LevelButton _parent;

    public ButtonPlatform(Vector2 position, int width, LevelButton parent)
        : base(position, width, safe: false)
    {
        _parent = parent;

        // Make it slightly thin so it feels like standing on the button surface
        Collider = new Hitbox(width, 4, 0, 0);

        Depth = Depths.Below - 1; // Just in front of the button visuals
    }

    public override void Render()
    {
        // Don't render anything - the LevelButton handles all rendering
    }
}

