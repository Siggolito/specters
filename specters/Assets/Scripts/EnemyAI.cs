using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// A guard that has to actually notice you before it fights, then fights from cover.
///
/// Detection is gradual: awareness fills only while you are inside the view cone,
/// unobstructed, AND standing in enough light. Darkness both slows the fill and
/// shortens the effective view range, so an unlit player can walk much closer.
/// Below <see cref="invisibleAtOrBelow"/> the guard cannot see you at all.
///
///   Idle        — slow head sweep, unaware
///   Suspicious  — glimpsed something, turns to look, does not shoot
///   MoveToCover — committed; running to a spot that breaks your line of sight
///   InCover     — hunkered down, waiting out the reload beat
///   Peek        — leaning out of cover to fire, then ducking back
///
/// Cover is found procedurally by sampling the NavMesh around the guard: a spot only
/// counts if it is hidden from you AND has a sideways lean that is not, so the guard
/// always has somewhere to shoot from. With no cover available it stands and fights.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
public class EnemyAI : MonoBehaviour
{
    public enum State { Idle, Suspicious, MoveToCover, InCover, Peek }

    [Header("Rig")]
    [Tooltip("Where the guard looks and shoots from. Defaults to a child named 'Eye'.")]
    [SerializeField] private Transform eye;
    [Tooltip("Where shots originate. Defaults to the eye.")]
    [SerializeField] private Transform muzzle;

    [Header("Target")]
    [Tooltip("Leave empty to find the object tagged Player.")]
    [SerializeField] private Transform target;

    [Header("Vision")]
    [SerializeField] private float viewRange = 25f;
    [Tooltip("Full width of the view cone, in degrees.")]
    [SerializeField] private float viewAngle = 110f;
    [SerializeField] private LayerMask sightBlockers = ~0;

    [Header("Darkness")]
    [Tooltip("At or below this light level the player is completely invisible.")]
    [Range(0f, 1f)] [SerializeField] private float invisibleAtOrBelow = 0.2f;
    [Tooltip("At or above this light level the player is spotted at full speed.")]
    [Range(0f, 1f)] [SerializeField] private float fullyVisibleAt = 0.6f;
    [Tooltip("Fraction of the view range that survives total darkness.")]
    [Range(0.05f, 1f)] [SerializeField] private float darkRangeMultiplier = 0.3f;

    [Header("Detection")]
    [Tooltip("Seconds of clear, well-lit sight at point-blank range to go from unaware to alert.")]
    [SerializeField] private float timeToDetectClose = 0.5f;
    [Tooltip("Same, but at the edge of the view range.")]
    [SerializeField] private float timeToDetectFar = 2.6f;
    [Tooltip("Awareness at which the guard starts investigating.")]
    [Range(0f, 1f)] [SerializeField] private float suspicionThreshold = 0.45f;
    [SerializeField] private float awarenessDecayPerSecond = 0.25f;
    [Tooltip("Seconds after losing sight before awareness starts falling.")]
    [SerializeField] private float decayGraceSeconds = 1.2f;

    [Header("Aggro")]
    [Tooltip("Once committed to a fight he stays in it this long with ZERO contact before giving up. Ducking behind cover does not count as losing you.")]
    [SerializeField] private float loseAggroAfterSeconds = 12f;
    [Tooltip("With no contact for this long he relocates to fresh cover nearer your last known position instead of waiting you out.")]
    [SerializeField] private float pushUpAfterSeconds = 4f;
    [Tooltip("Abandon a peek position if he cannot get the angle within this long.")]
    [SerializeField] private float peekApproachTimeout = 3f;

    [Header("Hearing")]
    [SerializeField] private bool canHear = true;
    [Tooltip("Multiplies the noise radius the player reports. Above 1 = sharp ears.")]
    [SerializeField] private float hearingSensitivity = 1f;
    [Tooltip("Sound still travels through walls, but only this fraction as far.")]
    [Range(0f, 1f)] [SerializeField] private float muffledThroughWalls = 0.6f;
    [Tooltip("Seconds to react to a sound heard at the very edge of its radius.")]
    [SerializeField] private float timeToNoticeSound = 1.1f;
    [Tooltip("Sound alone cannot push awareness past this — he still has to see you to fully engage.")]
    [Range(0f, 1f)] [SerializeField] private float hearingAwarenessCap = 0.8f;

    [Header("Proximity")]
    [Tooltip("Inside this radius he finds you regardless of light or view cone. Walls still block it.")]
    [SerializeField] private float proximityRadius = 3.5f;
    [SerializeField] private float timeToNoticeProximity = 0.35f;

    [Header("Investigating")]
    [Tooltip("Walk to the last place you were sensed instead of just staring at it.")]
    [SerializeField] private bool investigate = true;
    [Tooltip("How close to get to the last known position before stopping.")]
    [SerializeField] private float investigateStopDistance = 1.5f;

    [Header("Cover")]
    [SerializeField] private bool useCover = true;
    [SerializeField] private float coverSearchRadius = 14f;
    [Tooltip("How many NavMesh points to test per search.")]
    [SerializeField] private int coverSamples = 56;
    [Tooltip("Body height that has to be hidden from the threat for a spot to count as cover.")]
    [SerializeField] private float coverCheckHeight = 1.2f;
    [Tooltip("Sideways step out of cover to take a shot.")]
    [SerializeField] private float peekOffset = 0.9f;
    [Tooltip("Distance the guard likes to hold from the target.")]
    [SerializeField] private float idealEngageDistance = 12f;
    [Tooltip("Never pick cover closer to the target than this.")]
    [SerializeField] private float minEngageDistance = 4f;
    [Tooltip("How often the guard checks whether its cover still works.")]
    [SerializeField] private float coverReevaluateInterval = 2f;

    [Header("Cover Rhythm")]
    [Tooltip("Seconds hunkered down before leaning out again.")]
    [SerializeField] private float coverHoldSeconds = 1.4f;
    [Tooltip("Seconds spent leaning out and shooting.")]
    [SerializeField] private float peekSeconds = 1.8f;
    [SerializeField] private float moveSpeed = 4.2f;
    [SerializeField] private float arriveDistance = 0.4f;

    [Header("Combat")]
    [Tooltip("Beat between leaning out and the first shot, so you get a chance to react.")]
    [SerializeField] private float aimDelay = 0.4f;
    [SerializeField] private float fireInterval = 0.75f;
    [SerializeField] private float damage = 12f;
    [SerializeField] private float spreadDegrees = 3.5f;
    [SerializeField] private float weaponRange = 60f;
    [SerializeField] private Color tracerColor = new Color(1f, 0.85f, 0.4f);
    [SerializeField] private float tracerSeconds = 0.06f;

    [Header("Turning")]
    [SerializeField] private float turnSpeed = 240f;
    [SerializeField] private float idleScanSpeed = 22f;
    [SerializeField] private float idleScanArc = 55f;

    [Header("Debug")]
    [SerializeField] private bool showAwarenessHud = true;
    [SerializeField] private bool drawGizmos = true;

    public State Current { get; private set; } = State.Idle;
    public float Awareness { get; private set; }
    public bool Engaged => Current == State.MoveToCover || Current == State.InCover || Current == State.Peek;

    private NavMeshAgent agent;
    private Transform targetRoot;
    private LightSampler targetLight;
    private Collider ownCollider;

    private NoiseEmitter targetNoise;
    private CharacterController targetController;

    private Vector3 lastKnownPos;
    private float lastSensedTime = -999f;
    private float aimTimer;
    private float fireTimer;
    private float homeYaw;
    private Vector3 homePos;
    private bool sawTargetThisFrame;
    private bool heardTargetThisFrame;
    private bool proximityThisFrame;
    private float visibilityThisFrame;

    /// <summary>He can shoot at what he can see, or at what is right on top of him.</summary>
    private bool CanEngageTarget => sawTargetThisFrame || proximityThisFrame;

    private Vector3 coverHidePos;
    private Vector3 coverPeekPos;
    private bool hasCover;
    private float coverTimer;
    private float peekTimer;
    private float reevaluateTimer;
    private bool peekArmed;              // true once he is actually in position to shoot
    private float peekApproachTimer;
    private float lastCoverChangeTime = -999f;

    private bool inCombat;
    private float timeSinceContact;

    /// <summary>True while he is committed to a fight, whether or not he can see you.</summary>
    public bool InCombat => inCombat;

    private static Material tracerMat;
    private Texture2D px;
    private GUIStyle labelStyle;

    private void Awake()
    {
        if (eye == null)
        {
            Transform found = transform.Find("Eye");
            eye = found != null ? found : transform;
        }
        if (muzzle == null) muzzle = eye;

        if (target == null)
        {
            var player = GameObject.FindGameObjectWithTag("Player");
            if (player != null) target = player.transform;
        }
        if (target != null)
        {
            targetRoot = target.root;
            targetLight = target.GetComponentInParent<LightSampler>();
            if (targetLight == null) targetLight = target.GetComponentInChildren<LightSampler>();
            targetNoise = target.GetComponentInParent<NoiseEmitter>();
            if (targetNoise == null) targetNoise = target.GetComponentInChildren<NoiseEmitter>();
            targetController = target.GetComponentInParent<CharacterController>();
        }

        agent = GetComponent<NavMeshAgent>();
        agent.updateRotation = false;          // rotation is driven by FaceToward
        agent.speed = moveSpeed;
        agent.stoppingDistance = arriveDistance;

        ownCollider = GetComponentInChildren<Collider>();
        homeYaw = transform.eulerAngles.y;
        homePos = transform.position;
        lastKnownPos = transform.position + transform.forward * 5f;
        coverHidePos = coverPeekPos = transform.position;
    }

    private void OnDestroy()
    {
        if (px != null) Destroy(px);
    }

    private void Update()
    {
        float dt = Time.deltaTime;
        if (target == null) return;

        UpdateSenses(dt);
        UpdateState(dt);
    }

    // ---- Senses ---------------------------------------------------------

    /// <summary>
    /// Three senses feed one awareness meter. Sight is gated by the view cone, walls and
    /// light. Proximity ignores cone and light but not walls. Hearing ignores all three
    /// but is capped, so noise alone makes him come looking rather than open fire.
    /// The strongest sense that frame sets the fill rate; the weakest sets nothing.
    /// </summary>
    private void UpdateSenses(float dt)
    {
        visibilityThisFrame = targetLight != null
            ? Mathf.Clamp01(Mathf.InverseLerp(invisibleAtOrBelow, fullyVisibleAt, targetLight.Level01))
            : 1f;

        Vector3 targetPoint = TargetPoint();
        float gain = 0f;
        float cap = 0f;
        Vector3 sensedAt = lastKnownPos;

        // --- sight ---
        sawTargetThisFrame = CanSeeTarget(out float sightDistance);
        if (sawTargetThisFrame)
        {
            float effectiveRange = viewRange * Mathf.Lerp(darkRangeMultiplier, 1f, visibilityThisFrame);
            float t = Mathf.Clamp01(sightDistance / Mathf.Max(0.01f, effectiveRange));
            float seconds = Mathf.Lerp(timeToDetectClose, timeToDetectFar, t);

            gain = visibilityThisFrame / Mathf.Max(0.01f, seconds);
            cap = 1f;
            sensedAt = targetPoint;
        }

        // --- proximity: too close to miss, whatever the light or the facing ---
        proximityThisFrame = Vector3.Distance(eye.position, targetPoint) <= proximityRadius
                             && !BlockedByGeometry(eye.position, targetPoint);
        if (proximityThisFrame)
        {
            gain = Mathf.Max(gain, 1f / Mathf.Max(0.01f, timeToNoticeProximity));
            cap = 1f;
            sensedAt = targetPoint;
        }

        // --- hearing ---
        heardTargetThisFrame = false;
        if (canHear && targetNoise != null && targetNoise.Radius > 0.01f)
        {
            Vector3 noisePos = targetNoise.transform.position;
            float radius = targetNoise.Radius * hearingSensitivity;
            if (BlockedByGeometry(eye.position, noisePos + Vector3.up * 1.2f))
                radius *= muffledThroughWalls;

            float noiseDistance = Vector3.Distance(transform.position, noisePos);
            if (noiseDistance <= radius)
            {
                heardTargetThisFrame = true;
                float loudness = 1f - Mathf.Clamp01(noiseDistance / Mathf.Max(0.01f, radius));
                float rate = Mathf.Lerp(0.25f, 1f, loudness) / Mathf.Max(0.01f, timeToNoticeSound);

                if (Awareness < hearingAwarenessCap)
                {
                    gain = Mathf.Max(gain, rate);
                    cap = Mathf.Max(cap, hearingAwarenessCap);
                }
                if (!sawTargetThisFrame && !proximityThisFrame) sensedAt = noisePos;
            }
        }

        bool sensed = sawTargetThisFrame || proximityThisFrame || heardTargetThisFrame;
        if (sensed)
        {
            lastKnownPos = sensedAt;
            lastSensedTime = Time.time;
            timeSinceContact = 0f;
        }
        else
        {
            timeSinceContact += dt;
        }

        if (gain > 0f && Awareness < cap)
            Awareness = Mathf.Min(cap, Awareness + gain * dt);
        else if (!sensed && !inCombat && Time.time - lastSensedTime > decayGraceSeconds)
            Awareness = Mathf.Clamp01(Awareness - awarenessDecayPerSecond * dt);

        // Once he commits to a fight, breaking his own line of sight by ducking into
        // cover must not make him forget you. Aggro is held by a contact timer rather
        // than by the awareness meter, so only genuinely losing you for a long stretch
        // ends the fight.
        if (Awareness >= 1f) inCombat = true;

        if (inCombat)
        {
            Awareness = 1f;
            if (timeSinceContact > loseAggroAfterSeconds)
            {
                inCombat = false;
                Awareness = suspicionThreshold;      // still hunting, no longer fighting
                lastSensedTime = Time.time;
            }
        }
    }

    /// <summary>True if scenery sits between the two points. The target itself does not count.</summary>
    private bool BlockedByGeometry(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float dist = delta.magnitude;
        if (dist < 0.05f) return false;
        Vector3 dir = delta / dist;

        bool restore = DisableOwnCollider();
        try
        {
            if (Physics.Raycast(from, dir, out RaycastHit hit, dist + 0.1f,
                                sightBlockers, QueryTriggerInteraction.Ignore))
            {
                return !(targetRoot != null &&
                         (hit.transform == targetRoot || hit.transform.IsChildOf(targetRoot)));
            }
            return false;
        }
        finally
        {
            RestoreOwnCollider(restore);
        }
    }

    /// <summary>
    /// Centre of mass to look at and shoot at. Deliberately NOT the player's camera:
    /// that sits at the very crown of the capsule, inside the grazing band where
    /// skinWidth shrinks the query shape, so rays aimed there sail straight over.
    /// Using the controller bounds also tracks stance for free — crouching and going
    /// prone lower the aim point and shrink the target.
    /// </summary>
    private Vector3 TargetPoint()
    {
        if (targetController != null && targetController.enabled)
            return targetController.bounds.center;

        var cam = target.GetComponentInChildren<Camera>();
        return cam != null
            ? cam.transform.position - Vector3.up * 0.35f
            : target.position + Vector3.up * 1.1f;
    }

    private Vector3 ThreatPoint() => sawTargetThisFrame ? TargetPoint() : lastKnownPos;

    /// <summary>Cone test, darkness test, then a line-of-sight raycast.</summary>
    private bool CanSeeTarget(out float distance)
    {
        distance = 0f;
        if (visibilityThisFrame <= 0f) return false;   // pitch dark is genuinely unseeable

        Vector3 from = eye.position;
        Vector3 to = TargetPoint();
        Vector3 delta = to - from;
        distance = delta.magnitude;
        if (distance < 0.001f) return true;

        float effectiveRange = viewRange * Mathf.Lerp(darkRangeMultiplier, 1f, visibilityThisFrame);
        if (distance > effectiveRange) return false;

        Vector3 dir = delta / distance;
        if (Vector3.Angle(eye.forward, dir) > viewAngle * 0.5f) return false;

        bool restore = DisableOwnCollider();
        try
        {
            if (Physics.Raycast(from, dir, out RaycastHit hit, distance + 0.1f,
                                sightBlockers, QueryTriggerInteraction.Ignore))
            {
                bool hitTarget = targetRoot != null &&
                                 (hit.transform == targetRoot || hit.transform.IsChildOf(targetRoot));
                if (!hitTarget) return false;
            }
            return true;
        }
        finally
        {
            RestoreOwnCollider(restore);
        }
    }

    // ---- State ----------------------------------------------------------

    private void UpdateState(float dt)
    {
        switch (Current)
        {
            case State.Idle:
                ReturnToPost(dt);
                if (Awareness >= suspicionThreshold) Current = State.Suspicious;
                break;

            case State.Suspicious:
                InvestigateLastKnown(dt);
                if (inCombat) EngageFromCover();
                else if (Awareness <= 0.001f) Current = State.Idle;
                break;

            case State.MoveToCover:
                FaceMovementOrThreat(dt);
                if (Disengaged()) break;

                // if you are in front of him on the way there, take the shot anyway
                if (CanEngageTarget) UpdateFiring(dt);

                // you may have moved; the spot he is running to might not be cover now
                reevaluateTimer -= dt;
                if (reevaluateTimer <= 0f && !CoverStillWorks()) { EngageFromCover(); break; }

                if (AgentArrived())
                {
                    StopAgent();
                    Current = State.InCover;
                    coverTimer = coverHoldSeconds;
                }
                break;

            case State.InCover:
                FaceToward(ThreatPoint(), dt);
                if (Disengaged()) break;

                // cover is compromised - return fire and go find better
                if (sawTargetThisFrame)
                {
                    UpdateFiring(dt);
                    EngageFromCover();
                    break;
                }

                reevaluateTimer -= dt;
                if (reevaluateTimer <= 0f && !CoverStillWorks()) { EngageFromCover(); break; }

                // he will not just sit there: no contact for a while and he advances
                if (timeSinceContact > pushUpAfterSeconds &&
                    Time.time - lastCoverChangeTime > pushUpAfterSeconds)
                {
                    AdvanceOnLastKnown();
                    break;
                }

                coverTimer -= dt;
                if (coverTimer <= 0f) LeanOut();
                break;

            case State.Peek:
                FaceToward(ThreatPoint(), dt);
                if (Disengaged()) break;

                UpdateFiring(dt);

                if (!hasCover) break;                 // nowhere to duck back to; stand and fight

                // The shooting window does not start until he is actually in position,
                // otherwise the walk out from cover eats the whole window and he ducks
                // back without ever firing.
                if (!peekArmed)
                {
                    if (CanEngageTarget || AgentArrived())
                    {
                        peekArmed = true;
                        peekTimer = peekSeconds;
                        aimTimer = aimDelay;
                    }
                    else
                    {
                        peekApproachTimer -= dt;
                        if (peekApproachTimer <= 0f) EngageFromCover();  // no angle here, move
                    }
                    break;
                }

                peekTimer -= dt;
                if (peekTimer <= 0f) DuckBack();
                break;
        }
    }

    /// <summary>Drops out of the fight only when aggro has genuinely timed out. True if it did.</summary>
    private bool Disengaged()
    {
        if (inCombat) return false;
        StopAgent();
        Current = State.Suspicious;
        return true;
    }

    private void EngageFromCover()
    {
        if (!TryTakeCover(idealEngageDistance, float.PositiveInfinity))
            FightInTheOpen();
    }

    /// <summary>
    /// Picks cover and starts running to it. <paramref name="mustBeCloserThan"/> is a hard
    /// ceiling on how far the spot may sit from the threat — used when advancing, so a
    /// push genuinely gains ground instead of being outvoted by the "prefer nearby" term.
    /// Returns false if nothing qualifies.
    /// </summary>
    private bool TryTakeCover(float preferredDistance, float mustBeCloserThan)
    {
        if (!useCover || agent == null || !agent.isOnNavMesh) return false;
        if (!FindCover(ThreatPoint(), preferredDistance, mustBeCloserThan,
                       out Vector3 hide, out Vector3 peek)) return false;

        reevaluateTimer = coverReevaluateInterval;
        lastCoverChangeTime = Time.time;
        aimTimer = aimDelay;
        fireTimer = 0f;
        peekArmed = false;

        hasCover = true;
        coverHidePos = hide;
        coverPeekPos = peek;
        SetDestination(hide);
        Current = State.MoveToCover;
        return true;
    }

    /// <summary>No cover anywhere — hold position and trade shots.</summary>
    private void FightInTheOpen()
    {
        reevaluateTimer = coverReevaluateInterval;
        lastCoverChangeTime = Time.time;
        aimTimer = aimDelay;
        fireTimer = 0f;

        hasCover = false;
        coverHidePos = coverPeekPos = transform.position;
        StopAgent();
        Current = State.Peek;
        peekArmed = true;
    }

    /// <summary>
    /// Steps out to shoot. The peek angle is re-derived against where you are NOW,
    /// not where you were when he ducked in — otherwise he leans out into empty air.
    /// </summary>
    private void LeanOut()
    {
        bool restore = DisableOwnCollider();
        try
        {
            if (FindPeekBeside(ThreatPoint(), coverHidePos, out Vector3 fresh))
                coverPeekPos = fresh;
        }
        finally
        {
            RestoreOwnCollider(restore);
        }

        SetDestination(coverPeekPos);
        Current = State.Peek;
        peekArmed = false;
        peekApproachTimer = peekApproachTimeout;
        aimTimer = aimDelay;
    }

    private void DuckBack()
    {
        SetDestination(coverHidePos);
        Current = State.InCover;
        coverTimer = coverHoldSeconds;
    }

    /// <summary>Relocates to cover that is genuinely closer to your last known position.</summary>
    private void AdvanceOnLastKnown()
    {
        float current = Vector3.Distance(transform.position, lastKnownPos);
        float preferred = Mathf.Max(minEngageDistance + 1f, current * 0.55f);

        if (TryTakeCover(preferred, current - 1.5f)) return;

        // nothing closer worth moving to - hold this spot and try again later
        lastCoverChangeTime = Time.time;
    }

    /// <summary>Walks back to the post it started at, then goes back to sweeping.</summary>
    private void ReturnToPost(float dt)
    {
        if (agent != null && agent.isOnNavMesh &&
            Vector3.Distance(transform.position, homePos) > 0.75f)
        {
            SetDestination(homePos);
            FaceMovementOrThreat(dt);
            return;
        }

        StopAgent();
        IdleScan(dt);
    }

    /// <summary>Goes and looks at whatever it last saw or heard.</summary>
    private void InvestigateLastKnown(float dt)
    {
        if (investigate && agent != null && agent.isOnNavMesh)
        {
            if (Vector3.Distance(transform.position, lastKnownPos) > investigateStopDistance)
                SetDestination(lastKnownPos);
            else
                StopAgent();
        }
        FaceMovementOrThreat(dt);
    }

    private void IdleScan(float dt)
    {
        float yaw = homeYaw + Mathf.Sin(Time.time * idleScanSpeed * Mathf.Deg2Rad) * idleScanArc;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
        if (eye != transform) eye.localRotation = Quaternion.identity;
    }

    private void FaceMovementOrThreat(float dt)
    {
        Vector3 v = agent != null ? agent.velocity : Vector3.zero;
        v.y = 0f;
        if (v.sqrMagnitude > 0.25f) FaceToward(transform.position + v.normalized * 5f, dt);
        else FaceToward(ThreatPoint(), dt);
    }

    private void FaceToward(Vector3 point, float dt)
    {
        Vector3 flat = point - transform.position;
        flat.y = 0f;
        if (flat.sqrMagnitude > 0.0001f)
        {
            Quaternion want = Quaternion.LookRotation(flat.normalized, Vector3.up);
            transform.rotation = Quaternion.RotateTowards(transform.rotation, want, turnSpeed * dt);
        }

        if (eye != transform)
        {
            Vector3 full = point - eye.position;
            if (full.sqrMagnitude > 0.0001f)
            {
                Quaternion eyeWant = Quaternion.LookRotation(full.normalized, Vector3.up);
                eye.rotation = Quaternion.RotateTowards(eye.rotation, eyeWant, turnSpeed * dt);
            }
        }
    }

    // ---- Cover ----------------------------------------------------------

    /// <summary>
    /// Samples NavMesh points in a spiral around the guard. A point qualifies only if
    /// the threat cannot see it AND a sideways lean from it can, so every cover spot
    /// comes with somewhere to shoot from.
    /// </summary>
    private bool FindCover(Vector3 threatEye, float preferredDistance, float mustBeCloserThan,
                           out Vector3 hide, out Vector3 peek)
    {
        hide = peek = transform.position;
        float best = float.NegativeInfinity;
        bool found = false;

        bool restore = DisableOwnCollider();   // the guard is not its own cover
        try
        {
            for (int i = 0; i < coverSamples; i++)
            {
                float t = (i + 0.5f) / coverSamples;
                float radius = Mathf.Sqrt(t) * coverSearchRadius;
                float angle = i * 2.39996323f;                       // golden angle, even spread
                Vector3 probe = transform.position +
                                new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;

                if (!NavMesh.SamplePosition(probe, out NavMeshHit navHit, 2f, NavMesh.AllAreas)) continue;
                Vector3 candidate = navHit.position;

                float distToThreat = Vector3.Distance(candidate, threatEye);
                if (distToThreat < minEngageDistance) continue;
                if (distToThreat > mustBeCloserThan) continue;
                if (!IsHiddenFrom(threatEye, candidate)) continue;

                if (!FindPeekBeside(threatEye, candidate, out Vector3 peekCandidate)) continue;

                float score = -Vector3.Distance(transform.position, candidate)
                              - Mathf.Abs(distToThreat - preferredDistance) * 0.8f;

                if (score > best)
                {
                    best = score;
                    hide = candidate;
                    peek = peekCandidate;
                    found = true;
                }
            }
        }
        finally
        {
            RestoreOwnCollider(restore);
        }

        return found;
    }

    private static readonly float[] PeekScales = { 0.6f, 1f, 1.7f };

    /// <summary>
    /// A lateral step from cover that actually has line of sight to the threat.
    /// Tries a few distances on each side, nearest first, so tight corners still work.
    /// </summary>
    private bool FindPeekBeside(Vector3 threatEye, Vector3 coverPos, out Vector3 peek)
    {
        peek = coverPos;
        Vector3 toThreat = threatEye - coverPos;
        toThreat.y = 0f;
        if (toThreat.sqrMagnitude < 0.0001f) return false;
        Vector3 side = Vector3.Cross(Vector3.up, toThreat.normalized);

        for (int i = 0; i < PeekScales.Length; i++)
        {
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 probe = coverPos + side * (peekOffset * PeekScales[i] * s);
                if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, 0.6f, NavMesh.AllAreas)) continue;
                if (IsHiddenFrom(threatEye, hit.position)) continue;   // still blind, no use for shooting
                peek = hit.position;
                return true;
            }
        }
        return false;
    }

    /// <summary>True if the threat cannot see a body standing at <paramref name="spot"/>.</summary>
    private bool IsHiddenFrom(Vector3 threatEye, Vector3 spot)
    {
        Vector3 chest = spot + Vector3.up * coverCheckHeight;
        Vector3 delta = chest - threatEye;
        float dist = delta.magnitude;
        if (dist < 0.05f) return false;

        Vector3 dir = delta / dist;
        // start clear of the threat's own collider
        Vector3 origin = threatEye + dir * 0.5f;
        float len = dist - 0.55f;
        if (len <= 0f) return false;

        return Physics.Raycast(origin, dir, len, sightBlockers, QueryTriggerInteraction.Ignore);
    }

    private bool CoverStillWorks()
    {
        reevaluateTimer = coverReevaluateInterval;
        if (!hasCover) return false;

        bool restore = DisableOwnCollider();
        try { return IsHiddenFrom(ThreatPoint(), coverHidePos); }
        finally { RestoreOwnCollider(restore); }
    }

    private void SetDestination(Vector3 p)
    {
        if (agent == null || !agent.isOnNavMesh) return;
        agent.isStopped = false;
        agent.SetDestination(p);
    }

    private void StopAgent()
    {
        if (agent == null || !agent.isOnNavMesh) return;
        agent.isStopped = true;
        agent.ResetPath();
    }

    private bool AgentArrived()
    {
        if (agent == null || !agent.isOnNavMesh) return true;
        if (agent.pathPending) return false;
        return agent.remainingDistance <= Mathf.Max(arriveDistance, agent.stoppingDistance);
    }

    // ---- Combat ---------------------------------------------------------

    private void UpdateFiring(float dt)
    {
        aimTimer -= dt;
        fireTimer -= dt;

        // only shoot at something it can actually see, or something right on top of it
        if (aimTimer > 0f || !CanEngageTarget || fireTimer > 0f) return;

        Fire();
        fireTimer = fireInterval;
    }

    private void Fire()
    {
        Vector3 origin = muzzle.position;
        Vector3 dir = (TargetPoint() - origin).normalized;

        if (spreadDegrees > 0f)
        {
            dir = Quaternion.Euler(Random.Range(-spreadDegrees, spreadDegrees),
                                   Random.Range(-spreadDegrees, spreadDegrees), 0f) * dir;
        }

        Vector3 end = origin + dir * weaponRange;

        bool restore = DisableOwnCollider();
        try
        {
            if (Physics.Raycast(origin, dir, out RaycastHit hit, weaponRange,
                                ~0, QueryTriggerInteraction.Ignore))
            {
                end = hit.point;
                var health = hit.collider.GetComponentInParent<Health>();
                if (health != null) health.TakeDamage(damage);
            }
        }
        finally
        {
            RestoreOwnCollider(restore);
        }

        SpawnTracer(origin, end);
    }

    private void SpawnTracer(Vector3 a, Vector3 b)
    {
        if (tracerSeconds <= 0f) return;

        if (tracerMat == null)
        {
            Shader sh = Shader.Find("Sprites/Default");
            if (sh == null) sh = Shader.Find("Unlit/Color");
            if (sh == null) return;
            tracerMat = new Material(sh) { hideFlags = HideFlags.DontSave };
        }

        var go = new GameObject("Tracer") { hideFlags = HideFlags.DontSave };
        var lr = go.AddComponent<LineRenderer>();
        lr.material = tracerMat;
        lr.startColor = lr.endColor = tracerColor;
        lr.startWidth = 0.035f;
        lr.endWidth = 0.01f;
        lr.positionCount = 2;
        lr.SetPosition(0, a);
        lr.SetPosition(1, b);
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        Destroy(go, tracerSeconds);
    }

    // ---- Collider juggling ----------------------------------------------

    private bool DisableOwnCollider()
    {
        if (ownCollider == null || !ownCollider.enabled) return false;
        ownCollider.enabled = false;
        return true;
    }

    private void RestoreOwnCollider(bool restore)
    {
        if (restore && ownCollider != null) ownCollider.enabled = true;
    }

    // ---- Readouts -------------------------------------------------------

    private void OnGUI()
    {
        if (!showAwarenessHud || Awareness <= 0.005f) return;

        if (px == null)
        {
            px = new Texture2D(1, 1);
            px.SetPixel(0, 0, Color.white);
            px.Apply();
        }
        if (labelStyle == null)
            labelStyle = new GUIStyle(GUI.skin.label)
            { fontSize = 12, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };

        float w = 180f, h = 8f;
        float x = (Screen.width - w) * 0.5f;
        float y = 42f;

        Color c = Engaged ? new Color(0.95f, 0.28f, 0.24f)
                : Current == State.Suspicious ? new Color(0.95f, 0.76f, 0.25f)
                                              : new Color(0.8f, 0.8f, 0.8f);

        Draw(x - 2f, y - 2f, w + 4f, h + 4f, new Color(0f, 0f, 0f, 0.7f));
        Draw(x, y, w * Awareness, h, c);

        labelStyle.normal.textColor = c;
        string label;
        switch (Current)
        {
            case State.MoveToCover: label = "TAKING COVER"; break;
            case State.InCover:     label = timeSinceContact > pushUpAfterSeconds ? "HUNTING" : "IN COVER"; break;
            case State.Peek:        label = !hasCover ? "ALERT" : peekArmed ? "FIRING" : "LEANING OUT"; break;
            case State.Suspicious:  label = "INVESTIGATING"; break;
            default:
                label = proximityThisFrame ? "RIGHT THERE"
                      : heardTargetThisFrame && !sawTargetThisFrame ? "HEARD SOMETHING"
                                                                    : "NOTICING...";
                break;
        }
        GUI.Label(new Rect(x, y - 20f, w, 18f), label, labelStyle);
    }

    private void Draw(float x, float y, float w, float h, Color c)
    {
        if (w <= 0f || h <= 0f) return;
        Color prev = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(new Rect(x, y, w, h), px);
        GUI.color = prev;
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;
        Transform e = eye != null ? eye : transform;

        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.65f);
        Vector3 l = Quaternion.Euler(0f, -viewAngle * 0.5f, 0f) * e.forward;
        Vector3 r = Quaternion.Euler(0f, viewAngle * 0.5f, 0f) * e.forward;
        Gizmos.DrawRay(e.position, l * viewRange);
        Gizmos.DrawRay(e.position, r * viewRange);

        Gizmos.color = new Color(1f, 0.9f, 0.2f, 0.15f);
        Gizmos.DrawWireSphere(e.position, viewRange);
        Gizmos.color = new Color(0.3f, 0.6f, 1f, 0.4f);
        Gizmos.DrawWireSphere(e.position, viewRange * darkRangeMultiplier);

        Gizmos.color = new Color(1f, 0.25f, 0.25f, 0.7f);      // "finds you regardless" bubble
        Gizmos.DrawWireSphere(e.position, proximityRadius);

        if (Application.isPlaying && hasCover)
        {
            Gizmos.color = Color.cyan;
            Gizmos.DrawWireCube(coverHidePos + Vector3.up * 0.1f, new Vector3(0.6f, 0.2f, 0.6f));
            Gizmos.color = Color.magenta;
            Gizmos.DrawWireCube(coverPeekPos + Vector3.up * 0.1f, new Vector3(0.5f, 0.2f, 0.5f));
            Gizmos.color = Color.white;
            Gizmos.DrawLine(coverHidePos, coverPeekPos);
        }
    }
}
