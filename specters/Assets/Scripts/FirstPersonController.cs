using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// MW2-campaign-style first person controller: weighty acceleration-based movement,
/// smoothed mouse look, sprint, a stance system (stand / crouch / prone), a
/// sprint-into-slide with momentum and camera roll, auto/manual vaulting over
/// low obstacles (up to about half the character), a ledge grab you can hang from
/// on anything taller, subtle head bob, and an optional scripted "look around"
/// intro before the player takes control.
///
/// Controls:
///   WASD            move
///   Mouse           look
///   Left Shift      sprint (standing only)
///   Space           stand up / vault / grab ledge  (there is no jump)
///   Left Ctrl / C   crouch (hold) — tap while sprinting to slide
///   Z               toggle prone (tap during a slide to end it prone)
///   Esc             toggle cursor lock
///
/// While hanging from a ledge:
///   W (hold)        pull up to peek over the lip; release to sink back
///   A / D           shimmy along the ledge
///   Space           climb up over the ledge
///   S / Ctrl / Z    let go
///
/// Uses the new Input System low-level API (Keyboard.current / Mouse.current),
/// so it needs no .inputactions asset wiring.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    private enum Stance { Stand, Crouch, Prone, Slide }

    [Header("References")]
    [Tooltip("Camera transform. Must be a child of this object, positioned at eye height.")]
    [SerializeField] private Transform cameraTransform;

    [Header("Look")]
    [SerializeField] private float lookSensitivity = 0.12f;   // degrees per mouse delta unit
    [SerializeField] private float pitchMin = -89f;
    [SerializeField] private float pitchMax = 89f;
    [Tooltip("Seconds of mouse smoothing. 0 = raw/crisp, ~0.05 = CoD-ish glide.")]
    [SerializeField] private float lookSmoothing = 0.04f;

    [Header("Move Speeds")]
    [SerializeField] private float walkSpeed = 4.0f;
    [SerializeField] private float sprintSpeed = 7.0f;
    [SerializeField] private float crouchSpeed = 2.0f;
    [SerializeField] private float proneSpeed = 1.1f;

    [Header("Move Feel")]
    [SerializeField] private float groundAcceleration = 16f;
    [SerializeField] private float groundFriction = 12f;
    [SerializeField] private float airAcceleration = 3.5f;

    [Header("Jump / Gravity")]
    [Tooltip("Off by default — traversal is vault-and-climb, not jump-based. Gravity still applies.")]
    [SerializeField] private bool jumpEnabled = false;
    [SerializeField] private float jumpHeight = 1.1f;
    [SerializeField] private float gravity = -18f;
    [SerializeField] private float coyoteTime = 0.12f;

    [Header("Stances")]
    [SerializeField] private float standHeight = 1.8f;
    [SerializeField] private float crouchHeight = 1.15f;
    [SerializeField] private float proneHeight = 0.7f;
    [SerializeField] private float slideHeight = 0.95f;
    [SerializeField] private float eyeOffsetFromTop = 0.15f;
    [SerializeField] private float stanceLerpSpeed = 12f;

    [Header("Slide")]
    [Tooltip("Speed the slide launches at. Also gets boosted by current momentum.")]
    [SerializeField] private float slideImpulse = 9.5f;
    [SerializeField] private float slideFriction = 4.5f;
    [SerializeField] private float slideMinDuration = 0.3f;
    [SerializeField] private float slideMaxDuration = 0.95f;
    [Tooltip("Slide ends once it decays below this speed (after the min duration).")]
    [SerializeField] private float slideEndSpeed = 2.4f;
    [SerializeField] private float slideSteer = 2.6f;         // radians/sec the slide direction can bend
    [SerializeField] private float slideCooldown = 0.7f;
    [SerializeField] private float slideCameraRoll = 5f;      // degrees of camera tilt during a slide
    [Tooltip("Minimum planar speed (as a fraction of sprint speed) required to start a slide.")]
    [SerializeField] private float slideEntryFraction = 0.8f;

    [Header("Vault")]
    [SerializeField] private bool vaultEnabled = true;
    [Tooltip("Vault automatically when you run into a valid obstacle with forward held (no Space needed).")]
    [SerializeField] private bool autoVault = true;
    [Tooltip("Lowest obstacle top that triggers a vault, in metres above the feet (keep above the CharacterController step offset).")]
    [SerializeField] private float vaultMinHeight = 0.4f;
    [Tooltip("Tallest obstacle that can be vaulted, as a fraction of stand height. 0.5 = half the character; taller obstacles just block you.")]
    [SerializeField] private float vaultMaxHeightFraction = 0.5f;
    [Tooltip("How far in front of the body to look for an obstacle.")]
    [SerializeField] private float vaultReach = 0.7f;
    [Tooltip("An obstacle no deeper than this is cleared in one motion; deeper ones are climbed onto.")]
    [SerializeField] private float vaultOverMaxDepth = 1.4f;
    [SerializeField] private float vaultDuration = 0.5f;
    [Tooltip("Forward speed handed back to the player when the vault finishes. 0 = stop dead.")]
    [SerializeField] private float vaultExitSpeed = 1.2f;
    [SerializeField] private LayerMask vaultMask = ~0;
    [Tooltip("Fully lock movement AND mouse look during a vault so the move is committed.")]
    [SerializeField] private bool lockLookDuringVault = true;
    [Tooltip("Shapes the vault motion over its duration (X and Y both 0..1).")]
    [SerializeField] private AnimationCurve vaultProfile = new AnimationCurve(
        new Keyframe(0f, 0f, 0f, 0.6f),
        new Keyframe(0.35f, 0.28f),
        new Keyframe(1f, 1f, 1.4f, 0f));
    [Tooltip("Degrees the camera leans and dips through the vault animation.")]
    [SerializeField] private float vaultCameraLean = 5f;
    [SerializeField] private float vaultCameraDip = 0.14f;

    [Header("Ledge Grab")]
    [SerializeField] private bool ledgeGrabEnabled = true;
    [Tooltip("Catch ledges automatically while airborne and pushing forward into the wall.")]
    [SerializeField] private bool autoLedgeGrab = true;
    [Tooltip("Highest ledge the player can catch, in metres above the feet. The floor is wherever vaulting stops.")]
    [SerializeField] private float ledgeGrabMaxHeight = 2.4f;
    [Tooltip("How far in front of the body to look for a ledge.")]
    [SerializeField] private float ledgeGrabReach = 0.75f;
    [Tooltip("Won't grab while rising faster than this, so you catch near the apex or on the way down.")]
    [SerializeField] private float ledgeGrabMaxRise = 2f;
    [Tooltip("Eye height relative to the ledge top while hanging. Negative = eyes just under the lip.")]
    [SerializeField] private float hangEyeOffset = -0.1f;
    [SerializeField] private float hangSnapSpeed = 14f;
    [Tooltip("How far left/right you can look away from the wall while hanging. 0 = free look.")]
    [SerializeField] private float hangYawClamp = 100f;
    [Tooltip("Sideways shimmy speed along the ledge, metres/sec.")]
    [SerializeField] private float shimmySpeed = 1.4f;
    [Tooltip("Camera roll leaning into a shimmy, degrees.")]
    [SerializeField] private float shimmyRoll = 3.5f;
    [SerializeField] private float hangSwayAmount = 0.02f;
    [SerializeField] private float hangSwayFrequency = 1.6f;
    [Tooltip("How far the body pulls up when peeking over the ledge with forward held.")]
    [SerializeField] private float peekRise = 0.45f;
    [Tooltip("Slight forward lean while peeking, so you look over the lip rather than through it.")]
    [SerializeField] private float peekLerpSpeed = 9f;
    [SerializeField] private float peekLean = 0.12f;
    [Tooltip("Camera jolt when you first catch a ledge.")]
    [SerializeField] private float grabImpactDip = 0.12f;
    [Tooltip("Seconds of assisted turn toward the wall right after catching a ledge.")]
    [SerializeField] private float hangSettleTime = 0.35f;
    [Tooltip("Duration of the pull-up climb onto the ledge.")]
    [SerializeField] private float ledgeClimbDuration = 0.65f;
    [SerializeField] private float ledgeDropPush = 1.2f;
    [Tooltip("Delay before you can catch a ledge again after dropping off one.")]
    [SerializeField] private float ledgeRegrabCooldown = 0.35f;

    [Header("Noise")]
    [Tooltip("How far movement can be heard, per stance, in metres. Scaled by how fast you are actually moving.")]
    [SerializeField] private float sprintNoiseRadius = 22f;
    [SerializeField] private float walkNoiseRadius = 13f;
    [SerializeField] private float crouchNoiseRadius = 4f;
    [SerializeField] private float proneNoiseRadius = 1.5f;
    [SerializeField] private float slideNoiseRadius = 18f;
    [SerializeField] private float vaultNoiseRadius = 10f;

    [Header("Head Bob")]
    [SerializeField] private bool headBob = true;
    [SerializeField] private float bobFrequency = 9f;
    [SerializeField] private float bobAmplitude = 0.035f;

    [Header("Intro")]
    [Tooltip("Play a short scripted 'look around' before handing control to the player.")]
    [SerializeField] private bool playIntro = true;
    [SerializeField] private float introDuration = 4f;
    [Tooltip("How far the intro sweep looks left/right and up, in degrees.")]
    [SerializeField] private float introYawSweep = 45f;
    [SerializeField] private float introStartPitch = 22f;

    private CharacterController controller;
    private float yaw;
    private float pitch;
    private Vector2 smoothedLook;
    private Vector3 horizontalVelocity;
    private float verticalVelocity;
    private float lastGroundedTime;
    private CollisionFlags lastCollisionFlags;

    private Stance stance = Stance.Stand;
    private float currentHeight;
    private float eyeBaseY;
    private float currentEyeY;
    private float targetRoll;
    private float currentRoll;

    private float slideTimer;
    private float slideCooldownTimer;
    private Vector3 slideDir;
    private bool endSlideProne;

    private bool isVaulting;
    private float vaultT;
    private Vector3 vaultStart;
    private Vector3 vaultMid;
    private Vector3 vaultEnd;
    private float vaultStartPitch;
    private float vaultCamY;
    private float vaultCooldownTimer;
    private float activeVaultDuration;

    private bool isHanging;
    private Vector3 hangPos;
    private Vector3 hangForward;      // horizontal direction into the wall
    private Vector3 hangWallPoint;
    private float hangLedgeY;
    private float ledgeCooldownTimer;
    private float hangPeek;           // 0 = hanging low, 1 = peeking over the lip
    private float hangSettleTimer;
    private float grabImpact;         // 1 on catch, decays to 0
    private float shimmyInput;        // smoothed -1..1, drives the lean

    private float bobTimer;
    private float bobValue;
    private float introTimer;
    private float introBaseYaw;
    private bool IntroActive => playIntro && introTimer < introDuration;

    private NoiseEmitter noise;

    /// <summary>How far the player can currently be heard, in metres.</summary>
    public float NoiseRadius { get; private set; }

    /// <summary>True while crouched or prone — the quiet stances.</summary>
    public bool IsSneaking => stance == Stance.Crouch || stance == Stance.Prone;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        noise = GetComponent<NoiseEmitter>();
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        yaw = transform.eulerAngles.y;
        introBaseYaw = yaw;
        pitch = IntroActive ? introStartPitch : 0f;

        currentHeight = standHeight;
        ApplyControllerHeight(standHeight);
        eyeBaseY = standHeight - eyeOffsetFromTop;
        currentEyeY = eyeBaseY;
        if (cameraTransform != null)
            cameraTransform.localPosition = new Vector3(0f, eyeBaseY, 0f);
    }

    private void OnEnable() => SetCursorLocked(true);

    private void OnDisable()
    {
        SetCursorLocked(false);
        isVaulting = false;
        isHanging = false;
        if (controller != null && !controller.enabled) controller.enabled = true;
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        slideCooldownTimer = Mathf.Max(0f, slideCooldownTimer - dt);
        vaultCooldownTimer = Mathf.Max(0f, vaultCooldownTimer - dt);
        ledgeCooldownTimer = Mathf.Max(0f, ledgeCooldownTimer - dt);

        if (isVaulting)
        {
            if (!IntroActive && !lockLookDuringVault) HandleLook(dt);
            UpdateVault(dt);
            HandleHeadBob(dt);
            ApplyCameraTransform(dt);
            return;
        }

        if (isHanging)
        {
            if (!IntroActive) HandleLook(dt);
            UpdateHang(dt);
            HandleHeadBob(dt);
            ApplyCameraTransform(dt);
            return;
        }

        if (IntroActive)
        {
            UpdateIntro(dt);
        }
        else
        {
            HandleLook(dt);
            HandleStanceInput();

            if (autoVault && (lastCollisionFlags & CollisionFlags.Sides) != 0)
            {
                var k = Keyboard.current;
                if (k != null && k.wKey.isPressed && TryStartVault())
                {
                    UpdateVault(dt);
                    ApplyCameraTransform(dt);
                    return;
                }
            }
        }

        UpdateStance(dt);
        HandleMovement(dt);

        if (!IntroActive && TryAutoLedgeGrab())
        {
            ApplyCameraTransform(dt);
            return;
        }

        HandleHeadBob(dt);
        ApplyCameraTransform(dt);

        if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame)
            SetCursorLocked(Cursor.lockState != CursorLockMode.Locked);
    }

    /// <summary>
    /// Publishes how loud the player currently is. Lives in LateUpdate so it still runs
    /// on the frames where Update returns early for a vault or a ledge hang.
    /// </summary>
    private void LateUpdate()
    {
        float radius;

        if (isHanging)
        {
            radius = Mathf.Abs(shimmyInput) > 0.05f ? crouchNoiseRadius : 0f;
        }
        else if (isVaulting)
        {
            radius = vaultNoiseRadius;
        }
        else
        {
            float speed = PlanarSpeed();
            float frac = Mathf.Clamp01(speed / Mathf.Max(0.01f, walkSpeed));

            switch (stance)
            {
                case Stance.Slide:
                    radius = slideNoiseRadius;
                    break;
                case Stance.Prone:
                    radius = proneNoiseRadius * frac;
                    break;
                case Stance.Crouch:
                    radius = crouchNoiseRadius * frac;
                    break;
                default:
                    bool sprinting = speed > walkSpeed * 1.05f;
                    radius = (sprinting ? sprintNoiseRadius : walkNoiseRadius) * frac;
                    break;
            }

            if (controller.enabled && !controller.isGrounded) radius *= 0.4f;   // airborne is quieter
        }

        NoiseRadius = radius;
        if (noise != null) noise.SetRadius(radius);
    }

    // ---- Intro ---------------------------------------------------------------

    private void UpdateIntro(float dt)
    {
        introTimer += dt;
        float t = Mathf.Clamp01(introTimer / introDuration);
        float settle = Mathf.SmoothStep(0f, 1f, t);
        float driftAmp = 1f - settle;

        float yawDrift = Mathf.Sin(t * Mathf.PI * 1.5f) * introYawSweep * driftAmp;
        yaw = introBaseYaw + yawDrift;
        pitch = Mathf.Lerp(introStartPitch, 0f, settle)
                + Mathf.Sin(t * Mathf.PI * 2.3f) * 6f * driftAmp;
        pitch = Mathf.Clamp(pitch, pitchMin, pitchMax);

        if (t >= 1f)
        {
            yaw = introBaseYaw;
            pitch = 0f;
        }
    }

    // ---- Look ---------------------------------------------------------------

    private void HandleLook(float dt)
    {
        Vector2 delta = Vector2.zero;
        if (Mouse.current != null && Cursor.lockState == CursorLockMode.Locked)
            delta = Mouse.current.delta.ReadValue();

        Vector2 target = delta * lookSensitivity;
        float k = lookSmoothing <= 0f ? 1f : 1f - Mathf.Exp(-dt / lookSmoothing);
        smoothedLook = Vector2.Lerp(smoothedLook, target, k);

        yaw += smoothedLook.x;
        pitch = Mathf.Clamp(pitch - smoothedLook.y, pitchMin, pitchMax);
    }

    // ---- Stance ------------------------------------------------------------

    private void HandleStanceInput()
    {
        var k = Keyboard.current;
        if (k == null) return;

        bool crouchPressed = k.leftCtrlKey.wasPressedThisFrame || k.cKey.wasPressedThisFrame;
        bool crouchHeld = k.leftCtrlKey.isPressed || k.cKey.isPressed;
        bool pronePressed = k.zKey.wasPressedThisFrame;

        float planarSpeed = PlanarSpeed();

        // Sprint + tap crouch -> slide
        if (stance == Stance.Stand && controller.isGrounded && crouchPressed
            && slideCooldownTimer <= 0f && planarSpeed >= sprintSpeed * slideEntryFraction)
        {
            StartSlide();
            return;
        }

        // Prone toggle
        if (pronePressed)
        {
            switch (stance)
            {
                case Stance.Prone:
                    if (HasHeadroom(standHeight)) stance = Stance.Stand;
                    break;
                case Stance.Slide:
                    endSlideProne = true;
                    break;
                default:
                    stance = Stance.Prone;
                    break;
            }
            return;
        }

        // The slide state machine drives its own exit; don't fight it here.
        if (stance == Stance.Slide) return;

        // Crouch (hold-to-crouch)
        if (stance == Stance.Prone)
        {
            if (crouchPressed && HasHeadroom(crouchHeight)) stance = Stance.Crouch;
            return;
        }

        if (crouchHeld)
            stance = Stance.Crouch;
        else if (stance == Stance.Crouch && HasHeadroom(standHeight))
            stance = Stance.Stand;
    }

    private void UpdateStance(float dt)
    {
        if (stance == Stance.Slide)
        {
            slideTimer += dt;
            bool expired = slideTimer >= slideMaxDuration;
            bool tooSlow = slideTimer >= slideMinDuration && PlanarSpeed() <= slideEndSpeed;
            bool airborne = !controller.isGrounded && slideTimer > 0.15f;
            if (expired || tooSlow || airborne)
                EndSlide();
        }

        float targetHeight = HeightFor(stance);
        currentHeight = Mathf.Lerp(currentHeight, targetHeight, stanceLerpSpeed * dt);
        ApplyControllerHeight(currentHeight);
        eyeBaseY = Mathf.Max(0.25f, currentHeight - eyeOffsetFromTop);

        targetRoll = stance == Stance.Slide ? slideCameraRoll : 0f;
    }

    private void StartSlide()
    {
        Vector3 fwd = Flatten(transform.forward);
        Vector3 vel = Flatten(horizontalVelocity);
        slideDir = vel.sqrMagnitude > 0.01f ? vel.normalized : fwd;

        float boost = Mathf.Max(slideImpulse, vel.magnitude + 2f);
        horizontalVelocity = slideDir * boost;

        stance = Stance.Slide;
        slideTimer = 0f;
        endSlideProne = false;
    }

    private void EndSlide()
    {
        var k = Keyboard.current;
        bool crouchHeld = k != null && (k.leftCtrlKey.isPressed || k.cKey.isPressed);

        if (endSlideProne)
            stance = Stance.Prone;
        else if (crouchHeld || !HasHeadroom(standHeight))
            stance = Stance.Crouch;
        else
            stance = Stance.Stand;

        slideCooldownTimer = slideCooldown;
        endSlideProne = false;
    }

    private float HeightFor(Stance s)
    {
        switch (s)
        {
            case Stance.Crouch: return crouchHeight;
            case Stance.Prone:  return proneHeight;
            case Stance.Slide:  return slideHeight;
            default:            return standHeight;
        }
    }

    private void ApplyControllerHeight(float h)
    {
        controller.height = h;
        controller.center = new Vector3(0f, h * 0.5f, 0f);
    }

    /// <summary>True if there is space to grow the collider to <paramref name="targetHeight"/>.</summary>
    private bool HasHeadroom(float targetHeight)
    {
        float grow = targetHeight - currentHeight;
        if (grow <= 0.01f) return true;

        float r = Mathf.Max(0.05f, controller.radius - 0.01f);
        Vector3 origin = transform.position + Vector3.up * (currentHeight - r);
        return !Physics.SphereCast(origin, r, Vector3.up, out _, grow + 0.05f,
                                   ~0, QueryTriggerInteraction.Ignore);
    }

    // ---- Vault ----------------------------------------------------------

    /// <summary>
    /// Looks for an obstacle face in front, finds its top surface, and — if the top
    /// sits between <see cref="vaultMinHeight"/> and half the character height with
    /// room for the player — starts a scripted vault arc. Returns true if one began.
    /// </summary>
    private bool TryStartVault()
    {
        if (!vaultEnabled || isVaulting || vaultCooldownTimer > 0f || stance == Stance.Prone)
            return false;
        if (Time.time - lastGroundedTime > coyoteTime * 2f)
            return false;

        Vector3 fwd = Flatten(transform.forward);
        if (fwd.sqrMagnitude < 0.01f) return false;
        fwd.Normalize();

        bool wasEnabled = controller.enabled;
        controller.enabled = false;                 // exclude the player's own collider from all casts
        try
        {
            float feetY = transform.position.y;
            float minTop = feetY + vaultMinHeight;
            float maxTop = feetY + standHeight * vaultMaxHeightFraction;
            float r = controller.radius;

            // 1) an obstacle face, probed just below the lowest vaultable top
            Vector3 probe = new Vector3(transform.position.x,
                                        Mathf.Max(feetY + 0.1f, minTop - 0.1f),
                                        transform.position.z);
            if (!Physics.Raycast(probe, fwd, out RaycastHit wall, r + vaultReach,
                                 vaultMask, QueryTriggerInteraction.Ignore))
                return false;
            if (Vector3.Dot(wall.normal, -fwd) < 0.5f) return false;

            // 2) the top surface just past that face
            Vector3 topProbe = wall.point + fwd * 0.05f + Vector3.up * (maxTop - wall.point.y + 0.15f);
            if (!Physics.Raycast(topProbe, Vector3.down, out RaycastHit top, (maxTop - minTop) + 0.5f,
                                 vaultMask, QueryTriggerInteraction.Ignore))
                return false;                        // top is above the vault limit -> just a wall
            float ledgeY = top.point.y;
            if (ledgeY < minTop || ledgeY > maxTop) return false;
            if (Vector3.Dot(top.normal, Vector3.up) < 0.6f) return false;

            // 3) room for the player (at least crouched) just over the near edge
            Vector3 edgeStand = new Vector3(wall.point.x, ledgeY, wall.point.z) + fwd * (r + 0.05f);
            if (!FitsCrouched(edgeStand)) return false;

            // 4) thin obstacle -> clear it to the far ground; broad one -> land on top
            Vector3 landing = edgeStand;
            Vector3 overOrigin = edgeStand + fwd * vaultOverMaxDepth + Vector3.up * 0.5f;
            if (Physics.Raycast(overOrigin, Vector3.down, out RaycastHit far, standHeight + 1.5f,
                                vaultMask, QueryTriggerInteraction.Ignore)
                && far.point.y <= ledgeY + 0.05f
                && far.point.y >= feetY - standHeight)
            {
                Vector3 overTarget = far.point + Vector3.up * 0.02f;
                if (FitsCrouched(overTarget)) landing = overTarget;
            }

            // build the arc: start -> apex above the edge -> landing
            vaultStart = transform.position;
            vaultEnd = landing;
            float apexY = Mathf.Max(vaultStart.y + 0.1f, ledgeY + 0.15f);
            vaultMid = new Vector3(Mathf.Lerp(vaultStart.x, vaultEnd.x, 0.5f),
                                   apexY,
                                   Mathf.Lerp(vaultStart.z, vaultEnd.z, 0.5f));

            BeginVault(vaultDuration);
            return true;
        }
        finally
        {
            if (!isVaulting) controller.enabled = wasEnabled;
        }
    }

    private bool FitsCrouched(Vector3 feet) => Fits(feet, crouchHeight);

    /// <summary>True if a capsule of the given height standing on <paramref name="feet"/> is clear of geometry.</summary>
    private bool Fits(Vector3 feet, float height)
    {
        // Lift the capsule off the surface it stands on — a sphere exactly tangent to
        // the floor counts as an overlap, which would reject every valid landing spot.
        const float skin = 0.05f;
        float r = Mathf.Max(0.05f, controller.radius - 0.02f);
        Vector3 a = feet + Vector3.up * (r + skin);
        Vector3 b = feet + Vector3.up * Mathf.Max(height - r, r + skin);
        return !Physics.CheckCapsule(a, b, r, vaultMask, QueryTriggerInteraction.Ignore);
    }

    private void BeginVault(float duration)
    {
        isVaulting = true;
        isHanging = false;
        vaultT = 0f;
        activeVaultDuration = duration;
        vaultStartPitch = pitch;
        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        stance = Stance.Stand;
        controller.enabled = false;
    }

    /// <summary>
    /// Drives the whole committed vault "animation": a curve-shaped body arc plus a
    /// procedural camera plant/lean/rise. Movement input never reaches the controller
    /// while this runs (Update early-returns), and look is frozen unless
    /// <see cref="lockLookDuringVault"/> is off.
    /// </summary>
    private void UpdateVault(float dt)
    {
        vaultT += dt / Mathf.Max(0.05f, activeVaultDuration);
        float raw = Mathf.Clamp01(vaultT);
        float e = Mathf.Clamp01(vaultProfile.Evaluate(raw));

        // body arc: quadratic bezier start -> apex -> landing, timed by the curve
        float u = 1f - e;
        transform.position = u * u * vaultStart
                           + 2f * u * e * vaultMid
                           + e * e * vaultEnd;

        // camera animation
        float plant = Mathf.Sin(Mathf.Clamp01(raw / 0.4f) * Mathf.PI);   // 0 -> 1 -> 0 over first 40%
        float rise = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((raw - 0.3f) / 0.7f));
        vaultCamY = -vaultCameraDip * plant;
        targetRoll = Mathf.Sin(raw * Mathf.PI) * vaultCameraLean;

        if (lockLookDuringVault)
        {
            float pitchAnim = Mathf.Lerp(vaultStartPitch, 0f, rise) + plant * 9f;   // dip on the plant, level out over the top
            pitch = Mathf.LerpAngle(pitch, pitchAnim, 1f - Mathf.Exp(-14f * dt));
        }

        if (vaultT >= 1f) EndVault();
    }

    private void EndVault()
    {
        isVaulting = false;
        if (!controller.enabled) controller.enabled = true;
        vaultCooldownTimer = 0.25f;
        vaultCamY = 0f;
        targetRoll = 0f;

        Vector3 fwd = Flatten(transform.forward).normalized;
        horizontalVelocity = fwd * vaultExitSpeed;
        verticalVelocity = -1f;
        currentHeight = controller.height;
        currentEyeY = eyeBaseY;
    }

    // ---- Ledge grab ------------------------------------------------------

    private bool TryAutoLedgeGrab()
    {
        if (!ledgeGrabEnabled || !autoLedgeGrab || isHanging || isVaulting) return false;
        if (controller.isGrounded) return false;              // auto-grab is an airborne catch
        if (verticalVelocity > ledgeGrabMaxRise) return false;

        var k = Keyboard.current;
        if (k == null || !k.wKey.isPressed) return false;     // must be pushing into the wall
        return TryGrabLedge();
    }

    /// <summary>
    /// Looks for a wall whose top is above the vault ceiling but within arm's reach,
    /// and if the player can hang there, enters the hang state. Returns true on a catch.
    /// </summary>
    private bool TryGrabLedge()
    {
        if (!ledgeGrabEnabled || isHanging || isVaulting || ledgeCooldownTimer > 0f) return false;
        if (stance == Stance.Prone || stance == Stance.Slide) return false;

        Vector3 fwd = Flatten(transform.forward);
        if (fwd.sqrMagnitude < 0.01f) return false;
        fwd.Normalize();

        bool wasEnabled = controller.enabled;
        controller.enabled = false;                 // exclude the player's own collider from all casts
        try
        {
            float feetY = transform.position.y;
            float minTop = feetY + standHeight * vaultMaxHeightFraction;   // starts where vaulting gives up
            float maxTop = feetY + ledgeGrabMaxHeight;
            float r = controller.radius;

            // 1) a wall face — sweep a few heights, since a single chest-height ray
            //    sails clean over any ledge you have already jumped above
            if (!FindWallFace(fwd, feetY, r, out RaycastHit wall)) return false;

            // 2) the top of it, within reach
            Vector3 topProbe = wall.point + fwd * 0.05f + Vector3.up * (maxTop - wall.point.y + 0.15f);
            if (!Physics.Raycast(topProbe, Vector3.down, out RaycastHit top, (maxTop - minTop) + 0.5f,
                                 vaultMask, QueryTriggerInteraction.Ignore))
                return false;                        // top is out of reach
            float ledgeY = top.point.y;
            if (ledgeY < minTop || ledgeY > maxTop) return false;
            if (Vector3.Dot(top.normal, Vector3.up) < 0.6f) return false;

            // 3) somewhere to end up once we pull ourselves over
            Vector3 topStand = new Vector3(wall.point.x, ledgeY, wall.point.z) + fwd * (r + 0.1f);
            if (!FitsCrouched(topStand)) return false;

            // 4) room for the body to hang below the lip
            Vector3 pos = HangPositionFor(wall.point, ledgeY, fwd);
            if (!Fits(pos, standHeight)) return false;

            BeginHang(pos, fwd, ledgeY, wall.point);
            return true;
        }
        finally
        {
            if (!isHanging) controller.enabled = wasEnabled;
        }
    }

    /// <summary>
    /// Casts forward at several heights and returns the first facing wall found.
    /// A single fixed-height ray misses ledges the player has already risen above.
    /// </summary>
    private bool FindWallFace(Vector3 fwd, float feetY, float r, out RaycastHit wall)
    {
        float len = r + ledgeGrabReach;
        float[] heights =
        {
            feetY + ledgeGrabMaxHeight - 0.2f,
            feetY + standHeight * 0.7f,
            feetY + standHeight * 0.45f,
            feetY + 0.2f,
        };

        foreach (float h in heights)
        {
            Vector3 origin = new Vector3(transform.position.x, h, transform.position.z);
            if (Physics.Raycast(origin, fwd, out wall, len, vaultMask, QueryTriggerInteraction.Ignore)
                && Vector3.Dot(wall.normal, -fwd) >= 0.5f)
                return true;
        }

        wall = default;
        return false;
    }

    private Vector3 HangPositionFor(Vector3 wallPoint, float ledgeY, Vector3 fwd)
    {
        float eyeHeight = standHeight - eyeOffsetFromTop;
        return new Vector3(wallPoint.x, ledgeY + hangEyeOffset - eyeHeight, wallPoint.z)
               - fwd * (controller.radius + 0.02f);
    }

    private void BeginHang(Vector3 pos, Vector3 fwd, float ledgeY, Vector3 wallPoint)
    {
        isHanging = true;
        hangPos = pos;
        hangForward = fwd;
        hangLedgeY = ledgeY;
        hangWallPoint = wallPoint;

        horizontalVelocity = Vector3.zero;
        verticalVelocity = 0f;
        stance = Stance.Stand;
        currentHeight = standHeight;
        ApplyControllerHeight(standHeight);
        eyeBaseY = standHeight - eyeOffsetFromTop;

        hangPeek = 0f;
        hangSettleTimer = hangSettleTime;
        grabImpact = 1f;
        shimmyInput = 0f;

        controller.enabled = false;
    }

    /// <summary>
    /// The hang is a state, not a canned move: you stay on the ledge until you climb
    /// up or let go, and can look around and shimmy along it in the meantime.
    /// </summary>
    private void UpdateHang(float dt)
    {
        var k = Keyboard.current;

        // W pulls you up to peek over the lip; releasing sinks you back onto the hang
        bool peekHeld = k != null && k.wKey.isPressed;
        hangPeek = Mathf.MoveTowards(hangPeek, peekHeld ? 1f : 0f, peekLerpSpeed * dt);
        float peek = Mathf.SmoothStep(0f, 1f, hangPeek);

        // shimmy along the ledge, re-probing so corners and gaps stop you
        float shimmyRaw = k == null ? 0f
                        : (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
        shimmyInput = Mathf.Lerp(shimmyInput, shimmyRaw, 1f - Mathf.Exp(-10f * dt));

        if (Mathf.Abs(shimmyRaw) > 0.01f)
        {
            Vector3 right = Vector3.Cross(Vector3.up, hangForward).normalized;
            Vector3 candidate = hangPos + right * (shimmyRaw * shimmySpeed * dt);
            if (ValidateLedgeAt(candidate, out Vector3 corrected, out Vector3 newWallPoint))
            {
                hangPos = corrected;
                hangWallPoint = newWallPoint;
            }
        }

        Vector3 target = hangPos
                       + Vector3.up * (peek * peekRise)
                       + hangForward * (peek * peekLean);
        transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-hangSnapSpeed * dt));

        // assisted turn toward the wall just after the catch, then a plain clamp
        float wallYaw = Mathf.Atan2(hangForward.x, hangForward.z) * Mathf.Rad2Deg;
        if (hangSettleTimer > 0f)
        {
            hangSettleTimer -= dt;
            yaw = Mathf.LerpAngle(yaw, wallYaw, 1f - Mathf.Exp(-6f * dt));
        }
        if (hangYawClamp > 0f)
        {
            float delta = Mathf.DeltaAngle(wallYaw, yaw);
            yaw = wallYaw + Mathf.Clamp(delta, -hangYawClamp, hangYawClamp);
        }

        // camera: layered idle sway (braced and still while peeking), a jolt on the
        // catch, and a roll leaning into the shimmy
        grabImpact = Mathf.MoveTowards(grabImpact, 0f, dt / 0.35f);
        float sway = (Mathf.Sin(Time.time * hangSwayFrequency) * 0.7f
                    + Mathf.Sin(Time.time * hangSwayFrequency * 2.3f) * 0.3f) * hangSwayAmount;
        vaultCamY = sway * (1f - peek) - grabImpactDip * grabImpact * grabImpact;
        targetRoll = shimmyInput * shimmyRoll;

        if (k == null) return;

        if (k.spaceKey.wasPressedThisFrame)
        {
            StartLedgeClimb();
            return;
        }

        if (k.sKey.wasPressedThisFrame || k.leftCtrlKey.wasPressedThisFrame
            || k.cKey.wasPressedThisFrame || k.zKey.wasPressedThisFrame)
            ReleaseLedge();
    }

    /// <summary>Re-probes the ledge from a shimmied-to position; fails at corners and gaps.</summary>
    private bool ValidateLedgeAt(Vector3 feet, out Vector3 corrected, out Vector3 wallPoint)
    {
        corrected = feet;
        wallPoint = hangWallPoint;
        float r = controller.radius;

        Vector3 probe = new Vector3(feet.x, hangLedgeY - 0.25f, feet.z);
        if (!Physics.Raycast(probe, hangForward, out RaycastHit wall, r + ledgeGrabReach,
                             vaultMask, QueryTriggerInteraction.Ignore))
            return false;
        if (Vector3.Dot(wall.normal, -hangForward) < 0.5f) return false;

        Vector3 topProbe = wall.point + hangForward * 0.05f + Vector3.up * 0.6f;
        if (!Physics.Raycast(topProbe, Vector3.down, out RaycastHit top, 1.2f,
                             vaultMask, QueryTriggerInteraction.Ignore))
            return false;
        if (Mathf.Abs(top.point.y - hangLedgeY) > 0.15f) return false;   // don't shimmy onto a different height

        Vector3 pos = HangPositionFor(wall.point, hangLedgeY, hangForward);
        if (!Fits(pos, standHeight)) return false;

        corrected = pos;
        wallPoint = wall.point;
        return true;
    }

    private void StartLedgeClimb()
    {
        Vector3 topStand = new Vector3(hangWallPoint.x, hangLedgeY, hangWallPoint.z)
                           + hangForward * (controller.radius + 0.1f);
        if (!FitsCrouched(topStand)) return;         // blocked up there — keep hanging

        vaultStart = transform.position;
        vaultEnd = topStand;
        float apexY = hangLedgeY + 0.2f;
        vaultMid = new Vector3(Mathf.Lerp(vaultStart.x, vaultEnd.x, 0.5f),
                               apexY,
                               Mathf.Lerp(vaultStart.z, vaultEnd.z, 0.5f));
        BeginVault(ledgeClimbDuration);
    }

    private void ReleaseLedge()
    {
        isHanging = false;
        if (!controller.enabled) controller.enabled = true;
        ledgeCooldownTimer = ledgeRegrabCooldown;
        vaultCamY = 0f;

        horizontalVelocity = -hangForward * ledgeDropPush;
        verticalVelocity = 0f;
        currentEyeY = eyeBaseY;
    }

    // ---- Movement --------------------------------------------------------

    private void HandleMovement(float dt)
    {
        bool grounded = controller.isGrounded;
        if (grounded) lastGroundedTime = Time.time;

        var kb = Keyboard.current;
        Vector2 input = IntroActive ? Vector2.zero : ReadMoveInput();
        Vector3 wishDir = transform.right * input.x + transform.forward * input.y;
        if (wishDir.sqrMagnitude > 1f) wishDir.Normalize();

        if (stance == Stance.Slide)
        {
            float sp = Mathf.MoveTowards(PlanarSpeed(), 0f, slideFriction * dt);
            if (wishDir.sqrMagnitude > 0.01f)
                slideDir = Vector3.RotateTowards(slideDir, wishDir, slideSteer * dt, 0f).normalized;
            horizontalVelocity = slideDir * sp;
        }
        else
        {
            bool sprinting = !IntroActive && kb != null && kb.leftShiftKey.isPressed
                             && input.y > 0.1f && stance == Stance.Stand;
            float targetSpeed = stance == Stance.Prone ? proneSpeed
                              : stance == Stance.Crouch ? crouchSpeed
                              : (sprinting ? sprintSpeed : walkSpeed);
            Vector3 targetVel = wishDir * targetSpeed;

            if (grounded)
            {
                float speed = horizontalVelocity.magnitude;
                if (speed > 0f)
                {
                    float drop = speed * groundFriction * dt;
                    horizontalVelocity *= Mathf.Max(speed - drop, 0f) / speed;
                }
                horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVel, groundAcceleration * dt);
            }
            else
            {
                horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, targetVel, airAcceleration * dt);
            }
        }

        if (grounded && verticalVelocity < 0f)
            verticalVelocity = -2f;

        bool canJump = Time.time - lastGroundedTime <= coyoteTime;
        if (!IntroActive && kb != null && kb.spaceKey.wasPressedThisFrame)
            HandleActionPress(canJump);

        verticalVelocity += gravity * dt;
        lastCollisionFlags = controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * dt);
    }

    /// <summary>
    /// Space is the traversal key: get up out of a stance, vault a low obstacle, or
    /// catch a ledge. It only jumps if <see cref="jumpEnabled"/> is switched back on.
    /// </summary>
    private void HandleActionPress(bool canJump)
    {
        switch (stance)
        {
            case Stance.Slide:
                EndSlide();
                break;
            case Stance.Crouch:
            case Stance.Prone:
                if (HasHeadroom(standHeight)) stance = Stance.Stand;
                break;
            default:
                if (TryStartVault()) return;
                if (TryGrabLedge()) return;
                if (jumpEnabled && canJump) DoJump();
                break;
        }
    }

    private void DoJump()
    {
        verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
        lastGroundedTime = -999f;
    }

    private static Vector2 ReadMoveInput()
    {
        var k = Keyboard.current;
        if (k == null) return Vector2.zero;
        float x = (k.dKey.isPressed ? 1f : 0f) - (k.aKey.isPressed ? 1f : 0f);
        float y = (k.wKey.isPressed ? 1f : 0f) - (k.sKey.isPressed ? 1f : 0f);
        return new Vector2(x, y);
    }

    // ---- Head bob -------------------------------------------------------

    private void HandleHeadBob(float dt)
    {
        bool canBob = headBob && !isVaulting && controller.enabled && controller.isGrounded
                      && (stance == Stance.Stand || stance == Stance.Crouch);
        if (canBob && PlanarSpeed() > 0.15f)
        {
            bobTimer += dt * bobFrequency * Mathf.Clamp01(PlanarSpeed() / walkSpeed);
            float amp = bobAmplitude * Mathf.Clamp(PlanarSpeed() / walkSpeed, 0.5f, 1.4f);
            bobValue = Mathf.Sin(bobTimer) * amp;
        }
        else
        {
            bobTimer = 0f;
            bobValue = Mathf.Lerp(bobValue, 0f, 10f * dt);
        }
    }

    // ---- Apply ---------------------------------------------------------

    private void ApplyCameraTransform(float dt)
    {
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (cameraTransform == null) return;

        float rollLerp = isVaulting ? 12f : 8f;
        currentRoll = Mathf.Lerp(currentRoll, targetRoll, rollLerp * dt);
        cameraTransform.localRotation = Quaternion.Euler(pitch, 0f, currentRoll);

        currentEyeY = Mathf.Lerp(currentEyeY, eyeBaseY, stanceLerpSpeed * dt);
        cameraTransform.localPosition = new Vector3(0f, currentEyeY + bobValue + vaultCamY, 0f);
    }

    // ---- Helpers -----------------------------------------------------

    private float PlanarSpeed() => Flatten(horizontalVelocity).magnitude;

    private static Vector3 Flatten(Vector3 v) => new Vector3(v.x, 0f, v.z);

    private static void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
