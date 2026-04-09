using System.Globalization;
using Sharp.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Sharp.Shared.CStrike;
using Sharp.Shared.Enums;
using Sharp.Shared.GameEntities;
using Sharp.Shared.HookParams;
using Sharp.Shared.Listeners;
using Sharp.Shared.Managers;
using Sharp.Shared.Objects;
using Sharp.Shared.Types;
using Sharp.Shared.Units;

namespace ParticleTest;

// ─────────────────────────────────────────────────────────────────────────────
//  Vector helpers (unchanged)
// ─────────────────────────────────────────────────────────────────────────────

public static class VectorExtensions
{
    public static string ToEKVString(this Vector vector)
    {
        return
            $"{vector.X.ToString("F6", CultureInfo.InvariantCulture)} {vector.Y.ToString("F6", CultureInfo.InvariantCulture)} {vector.Z.ToString("F6", CultureInfo.InvariantCulture)}";
    }

    public static Vector Lerp(this Vector from, Vector to, float t)
    {
        if (t < 0f) t = 0f;
        if (t > 1f) t = 1f;

        return new Vector(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Z + (to.Z - from.Z) * t
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Frame index constants for symbols_x.vtex
//  Verify all indices against the actual sprite sheet before shipping.
// ─────────────────────────────────────────────────────────────────────────────

public static class SymbolFrames
{
    public const int Colon  = 0;
    public const int Period = 1;
    public const int Slash  = 2;
    public const int Space  = 3;
    public const int Dash   = 4;
    public const int Plus   = 5;
}

// ─────────────────────────────────────────────────────────────────────────────
//  HudGlyph — one rendered character = one particle instance
//  Owns the control-point writes for that glyph each tick.
// ─────────────────────────────────────────────────────────────────────────────

public sealed class HudGlyph : IDisposable
{
    // Server CP slot → particle CP index assignments.
    // The server array has 4 slots (0–3); we map each to the particle CP the vpcf reads.
    // Slot 0 → CP16  (RGB color via C_OP_RemapCPtoVector)
    // Slot 1 → CP17  (alpha Y, self-illum brightness Z)
    // Slot 2 → CP32  (animation frame index X)
    // Slot 3 → CP33  (horizontal offset X, vertical offset Y)
    private const byte AssignColor      = 16;
    private const byte AssignAlpha      = 17;
    private const byte AssignFrame      = 32;
    private const byte AssignOffset     = 33;

    private readonly IBaseParticle _particle;
    private readonly ILogger _logger;
    private bool _firstApply = true;
    private bool _faulted;
    private bool _disposed;

    public HudGlyph(IBaseParticle particle, ILogger logger)
    {
        _particle = particle;
        _logger   = logger;
        SetupAssignments();
    }

    // Maps the 4 server slots to the particle CPs that the vpcf operators read.
    // Must be called once after spawn; the mapping persists for the entity's lifetime.
    private void SetupAssignments()
    {
        try
        {
            var a = _particle.GetServerControlPointAssignments();
            a[0] = AssignColor;
            a[1] = AssignAlpha;
            a[2] = AssignFrame;
            a[3] = AssignOffset;
            _logger.LogDebug(
                "[HudGlyph] Assignments set: slot0→CP{C} slot1→CP{A} slot2→CP{F} slot3→CP{O}",
                AssignColor, AssignAlpha, AssignFrame, AssignOffset);
        }
        catch (Exception ex)
        {
            _faulted = true;
            _logger.LogError(ex, "[HudGlyph] Failed to configure CP assignments — glyph disabled");
        }
    }

    /// <summary>
    /// Writes glyph state via the 4 server CP slots (mapped to particle CPs by assignments).
    ///   Slot 0 (→ CP16): RGB color 0–255
    ///   Slot 1 (→ CP17): Y = alpha 0–1, Z = self-illum brightness
    ///   Slot 2 (→ CP32): X = animation frame index
    ///   Slot 3 (→ CP33): X = horizontal offset, Y = vertical offset
    /// </summary>
    public void Apply(
        int frame,
        float offsetX,
        float offsetY,
        Color32 color,
        float brightness = 1f,
        float minSize = 0.02f)
    {
        if (_faulted) return;

        try
        {
            var cps = _particle.GetServerControlPoints();

            if (_firstApply)
            {
                _firstApply = false;
                _logger.LogInformation(
                    "[HudGlyph] First Apply — frame={Frame} offsetX={X:F3} offsetY={Y:F3} color=({R},{G},{B},{A}) brightness={Br:F2}",
                    frame, offsetX, offsetY, color.R, color.G, color.B, color.A, brightness);
            }

            cps[0] = new Vector(color.R, color.G, color.B);
            cps[1] = new Vector(0f, color.A / 255f, brightness);
            cps[2] = new Vector(frame, 0f, 0f);
            cps[3] = new Vector(offsetX, offsetY, 0f);
        }
        catch (Exception ex)
        {
            _faulted = true;
            _logger.LogError(ex,
                "[HudGlyph] Apply faulted — suppressing further calls. frame={Frame} offsetX={X:F3} offsetY={Y:F3}",
                frame, offsetX, offsetY);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _particle.Kill();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  SpeedHud — manages one player's speed display as particle glyphs
//  Format: "NNNN u/s" centered on screen
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SpeedHud : IDisposable
{
    // Screen-space X spacing between glyphs — tune to match sprite aspect ratio.
    // CP33 offsets are not true pixels; scale depends on world size and FOV.
    private const float GlyphWidth = 0.035f;

    // Vertical screen offset (CP33.Y). 0 = center of screen.
    private const float SpeedY = 0f;

    private readonly IEntityManager _entityManager;
    private readonly ILogger _logger;

    // Game-relative particle paths — must match what OnResourcePrecache registers
    private const string NumberVpcf    = "particles/numbers_x/number_x.vpcf";
    private const string SymbolVpcf    = "particles/symbols_x/symbol_x.vpcf";
    private const string LowercaseVpcf = "particles/lowercase_letters_x/lowercase_x.vpcf";

    // Growing glyph pool — never shrinks; excess glyphs hidden with alpha=0
    private readonly List<HudGlyph> _glyphs = new();

    // Reusable buffer to avoid per-tick list allocation
    private readonly List<(int Frame, string Vpcf)> _glyphBuffer = new();

    private bool _disposed;

    public SpeedHud(IEntityManager entityManager, ILogger logger)
    {
        _entityManager = entityManager;
        _logger        = logger;
    }

    /// <summary>
    /// Refreshes the speed display. Must be called every tick.
    /// </summary>
    /// <param name="speed">Scalar speed in units/s</param>
    public void Update(float speed)
    {
        BuildSpeedGlyphs((int)speed, _glyphBuffer);
        EnsureGlyphCount(_glyphBuffer.Count);

        // Light blue — matches surf HUD convention
        var color = new Color32(200, 230, 255, 220);

        float xCursor = -(_glyphBuffer.Count * GlyphWidth) / 2f;
        for (int i = 0; i < _glyphBuffer.Count; i++)
        {
            _glyphs[i].Apply(_glyphBuffer[i].Frame, xCursor, SpeedY, color);
            xCursor += GlyphWidth;
        }
    }

    // Builds the glyph sequence "NNNN u/s" into the provided buffer (clears first).
    // lowercase 'u' = frame 20  (a=0 … u=20)
    // lowercase 's' = frame 18  (a=0 … s=18)
    // Verify frame indices against the actual lowercase_letters_x.vtex sprite sheet.
    private void BuildSpeedGlyphs(int speed, List<(int Frame, string Vpcf)> buffer)
    {
        buffer.Clear();

        foreach (char c in speed.ToString())
            buffer.Add((c - '0', NumberVpcf));

        buffer.Add((SymbolFrames.Space, SymbolVpcf));
        buffer.Add((20, LowercaseVpcf)); // 'u'
        buffer.Add((SymbolFrames.Slash, SymbolVpcf));
        buffer.Add((18, LowercaseVpcf)); // 's'
    }

    private void EnsureGlyphCount(int needed)
    {
        while (_glyphs.Count < needed)
        {
            var particle = SpawnGlyphParticle(NumberVpcf);
            if (particle == null)
            {
                _logger.LogWarning("[SpeedHud] Failed to spawn glyph #{Index} — stopping pool growth", _glyphs.Count);
                break;
            }

            _glyphs.Add(new HudGlyph(particle, _logger));
        }

        // Push excess glyphs off-screen — cheaper than kill/respawn every tick
        for (int i = needed; i < _glyphs.Count; i++)
            _glyphs[i].Apply(0, 9999f, 9999f, new Color32(0, 0, 0, 0));
    }

    private IBaseParticle? SpawnGlyphParticle(string vpcf)
    {
        _logger.LogDebug("[SpeedHud] Spawning glyph particle: {Vpcf}", vpcf);

        var kv = new Dictionary<string, KeyValuesVariantValueItem>
        {
            { "effect_name", vpcf }
        };

        var particle = _entityManager.SpawnEntitySync<IBaseParticle>("info_particle_system", kv);
        if (particle == null)
        {
            _logger.LogWarning("[SpeedHud] SpawnEntitySync returned null for vpcf: {Vpcf}", vpcf);
            return null;
        }

        particle.DispatchSpawn();
        particle.AcceptInput("Activate");
        particle.AcceptInput("Start");

        return particle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var g in _glyphs)
            g.Dispose();

        _glyphs.Clear();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
//  Plugin module
// ─────────────────────────────────────────────────────────────────────────────

public class ParticleTest : IModSharpModule, IGameListener, IClientListener
{
    
    string IModSharpModule.DisplayName => "ParticleTest";
    string IModSharpModule.DisplayAuthor => "Retro";
    int IGameListener.ListenerVersion => IGameListener.ApiVersion;
    int IGameListener.ListenerPriority => 0;
    int IClientListener.ListenerVersion => IClientListener.ApiVersion;
    int IClientListener.ListenerPriority => 0;
    
    private readonly ISharedSystem _sharedSystem;
    private readonly string _sharpPath;
    private readonly IClientManager _clientManager;
    private readonly IEntityManager _entityManager;
    private readonly ILogger<ParticleTest> _logger;

    private string? _particleFile = "particles/ig/trail.vpcf";
    private string? _particle = "trail";

    private const int TrailLength = 16;

    // Only used for probing since this schema array doesn't expose Count in your setup
    private const int MaxProbeControlPoints = 64;

    private IBaseParticle? _activeParticle;
    private Queue<Vector>? _trailHistory;
    private PlayerSlot? _debugSlot;
    private int _controlPointCapacity;
    private readonly AddonManager _addonManager;
    private readonly IModSharp _modSharp;
    private readonly string _sharpAssets;

    // Per-player speed HUD instances — lazy-created in OnPostThink
    private readonly Dictionary<PlayerSlot, SpeedHud> _speedHuds = new();

    public ParticleTest(
        ISharedSystem sharedSystem,
        string dllPath,
        string sharpPath,
        Version version,
        IConfiguration configuration,
        bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _sharpPath = sharpPath;
        _clientManager = sharedSystem.GetClientManager();
        _entityManager = sharedSystem.GetEntityManager();
        _modSharp = sharedSystem.GetModSharp();

        _logger = sharedSystem.GetLoggerFactory().CreateLogger<ParticleTest>();
        _sharpAssets = Path.Combine(sharpPath, "assets");
        _addonManager = new AddonManager(sharedSystem, sharpPath, _sharpAssets, sharedSystem.GetLoggerFactory().CreateLogger<AddonManager>());
    }

    public bool Init()
    {
        _addonManager.Init();
        _clientManager.InstallClientListener(this);
        _sharedSystem.GetModSharp().InstallGameListener(this);
        _sharedSystem.GetHookManager().PlayerPostThink.InstallForward(OnPostThink);
        return true;
    }

    public void PostInit()
    {
        _addonManager.OnPostInit();
    }

    public void Shutdown()
    {
        // Dispose all player HUDs before tearing down the rest
        foreach (var hud in _speedHuds.Values)
            hud.Dispose();
        _speedHuds.Clear();

        _addonManager.Shutdown();
        _clientManager.RemoveClientListener(this);
        _sharedSystem.GetModSharp().RemoveGameListener(this);
        _sharedSystem.GetHookManager().PlayerPostThink.RemoveForward(OnPostThink);
    }

    public void OnResourcePrecache()
    {
        var assetPath = Path.Combine(_sharpPath, "assets");

        if (!Directory.Exists(assetPath))
        {
            _logger.LogWarning("Asset path does not exist: {Path}", assetPath);
            return;
        }

        var files = Directory.EnumerateFiles(assetPath, "*", SearchOption.AllDirectories);

        foreach (var file in files)
        {
            try
            {
                // Convert to relative game path
                var relative = file[(assetPath.Length + 1)..]
                    .Replace("\\", "/");

                // Strip compiled suffix ONLY if present (_c)
                var asset = relative.EndsWith("_c", StringComparison.OrdinalIgnoreCase)
                    ? relative[..^2]
                    : relative;

                _modSharp.PrecacheResource(asset);

                _logger.LogDebug("Precached: {Asset}", asset);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to precache file: {File}", file);
            }
        }

        _logger.LogInformation("Precached assets");
    }

    public void OnPostThink(IPlayerThinkForwardParams param)
    {
        var pawn = param.Pawn;
        if (pawn == null || !pawn.IsValid() || !pawn.IsAlive || param.Client.IsFakeClient)
            return;

        var controller = pawn.GetController();
        if (controller == null)
        {
            _logger.LogDebug("[ParticleTest] OnPostThink: pawn has no controller, skipping");
            return;
        }

        // ── Speed HUD (runs for every valid player every tick) ────────────────

        var slot = controller.PlayerSlot;

        if (!_speedHuds.TryGetValue(slot, out var hud))
        {
            _logger.LogInformation("[SpeedHud] Creating HUD for slot {Slot}", slot);
            hud = new SpeedHud(_entityManager, _logger);
            _speedHuds[slot] = hud;
        }

        var velocity = pawn.GetAbsVelocity();
        var speed    = velocity.Length();

        if (Environment.TickCount % 64 == 0)
            _logger.LogDebug("[SpeedHud] Slot={Slot} Velocity=({VX:F1},{VY:F1},{VZ:F1}) Speed={Speed:F1}",
                slot, velocity.X, velocity.Y, velocity.Z, speed);

        hud.Update(speed);

        // ── Trail debug (restricted to _debugSlot) ────────────────────────────

        if (_activeParticle == null || _trailHistory == null || _debugSlot == null)
            return;

        if (controller.PlayerSlot != _debugSlot)
            return;

        var current = pawn.GetAbsOrigin();

        _trailHistory.Enqueue(current);

        while (_trailHistory.Count > TrailLength)
            _trailHistory.Dequeue();

        var smoothed = _trailHistory.ToArray();
        for (int i = 1; i < smoothed.Length; i++)
        {
            smoothed[i] = smoothed[i].Lerp(smoothed[i - 1], 0.5f);
        }

        var writeCount = WriteControlPoints(_activeParticle, smoothed);

        if (Environment.TickCount % 64 == 0)
        {
            _logger.LogInformation(
                "[ParticleTest] Update: writing {Written}/{Requested} CPs | Capacity={Capacity} | History={History}",
                writeCount,
                smoothed.Length,
                _controlPointCapacity,
                _trailHistory.Count);
        }
    }

    private int WriteControlPoints(IBaseParticle particle, Vector[] values)
    {
        var cps = particle.GetServerControlPoints();
        var maxWrites = _controlPointCapacity > 0
            ? Math.Min(values.Length, _controlPointCapacity)
            : values.Length;

        var written = 0;

        for (int i = 0; i < maxWrites; i++)
        {
            try
            {
                cps[i] = values[i];
                written++;
            }
            catch (IndexOutOfRangeException)
            {
                _logger.LogWarning(
                    "[ParticleTest] CP write overflow at index {Index}. Wrote {Written} total.",
                    i,
                    written);

                _controlPointCapacity = written;
                break;
            }
        }

        return written;
    }
}
