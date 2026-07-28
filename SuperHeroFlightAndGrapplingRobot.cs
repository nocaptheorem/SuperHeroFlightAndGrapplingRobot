using Godot;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// =================================================================================
// CORE: KINEMATIC STATE
// =================================================================================
public struct RagdollState
{
  public Vector3 CenterOfMass;
  public Vector3 Velocity;
  public float TotalMass;
  public bool IsAirborne;
  public bool IsFallen;
}

// =================================================================================
// COMPONENT: QUATERNION ATTITUDE CONTROLLER
// =================================================================================
public class QuaternionAttitudeController
{
  public float Kp, Kd;
  public QuaternionAttitudeController(float p, float d) { Kp = p; Kd = d; }

  public Vector3 ComputeTorque(Quaternion currentRot, Quaternion targetRot, Vector3 currentAngVelGlobal)
  {
    Quaternion qErr = currentRot.Inverse() * targetRot;
    if (qErr.W < 0) qErr = new Quaternion(-qErr.X, -qErr.Y, -qErr.Z, -qErr.W);
    qErr = qErr.Normalized();

    Vector3 axis = new Vector3(qErr.X, qErr.Y, qErr.Z);
    float angle = 2.0f * Mathf.Acos(Mathf.Clamp(qErr.W, -1.0f, 1.0f));

    if (axis.LengthSquared() > 0.0001f) axis = axis.Normalized();
    else axis = Vector3.Zero;

    Vector3 globalAxis = currentRot * axis;
    return (globalAxis * angle * Kp) - (currentAngVelGlobal * Kd);
  }
}

/// <summary>
/// A Physics-based Active Ragdoll Controller with Dual Grappling Hooks,
/// Iron Man Flight Mechanics, FPS Debug Camera, and Procedural City Generation.
/// </summary>
[GlobalClass]
public partial class SuperHeroFlightAndGrapplingRobot : Skeleton3D
{
#region 1. Debug Configuration
  [ExportGroup("DEBUG")]
  [Export] public bool EnableDebugLogs = true;
  [Export] public bool DrawDebugGizmos = true;
  #endregion

  #region 2. System Links
  [ExportGroup("1. System Links")]
  [Export] public NodePath TargetSimulatorPath = null!;
  [Export] public Skeleton3D AnimationShadow = null!;

  private PhysicalBoneSimulator3D? _sim;
  private PhysicalBone3D? _hips;
  private MuscleGroup? _hipMuscle;
  private PhysicalBone3D? _chest;
  private PhysicalBone3D? _head;

  // Anchor points & Effectors
  private PhysicalBone3D? _grappleHandL, _grappleHandR;
  private PhysicalBone3D? _leftFoot, _rightFoot;
  private PhysicalBone3D? _leftUpLeg, _rightUpLeg, _leftArm, _rightArm;
  #endregion

  #region 3. Bio-Mechanics
  [ExportGroup("2. Bio-Mechanics (Structure)")]
  [Export] public float MuscleStiffness = 5000.0f;
  [Export] public float MuscleDamping = 100.0f;
  [Export] public float MaxMuscleTorque = 15000.0f;
  [Export(PropertyHint.Range, "0, 1.5")] public float GravityComp = 1.0f;

  [Export] public float ImpactRelaxationAngle = 1.2f;
  [Export] public float ImpactDampingSpike = 5.0f;
  #endregion

  #region 4. Balance & VMC Settings
  [ExportGroup("3. Core Stabilization (The Gyro)")]
  [Export] public float HipGyroStiffness = 900000.0f;
  [Export] public float HipGyroDamping = 900.0f;

  [ExportGroup("4. VMC Balance (The Legs)")]
  [Export] public float TargetHeight = 0.6f;
  [Export] public float CenterOfPressureOffset = 0.009f;
  [Export] public float SupportSpring = 5000.0f;
  [Export] public float SupportDamp = 300.0f;
  [Export] public float BalanceStiffness = 90000.0f;
  [Export] public float BalanceDamping = 400.0f;
  [Export] public float MaxForce = 40000.0f;
  #endregion

  #region 5. Recovery & Strategy
  [ExportGroup("5. Recovery")]
  [Export] public float FallenHeight = 0.45f;
  [Export] public float RecoveryStiffness = 150000.0f;
  [Export] public float RecoveryDamping = 8000.0f;

  [ExportGroup("6. Ankle Strategy")]
  [Export] public float AnkleStiffness = 90000.0f;
  [Export] public float AnkleDamping = 1000.0f;

  [ExportGroup("7. Physics Layers")]
  [Export(PropertyHint.Layers3DPhysics)] public uint GroundMask = 1;
  [Export(PropertyHint.Layers3DPhysics)] public uint PlayerLayer = 2;
  #endregion

  #region 6. Camera & Aerodynamics
  [ExportGroup("8. FPS Debug Camera")]
  [Export] public float MoveSpeed = 5.0f;
  [Export] public float MouseSensitivity = 0.003f;

  [ExportGroup("9. Aerodynamics")]
  [Export] public float AirDensity = 1.225f;
  [Export] public float BaseDragCoefficient = 0.4f;
  [Export] public float WindShearMultiplier = 1.0f;
  private Vector3 _ambientWind = new Vector3(0, 0, 0);
  #endregion

  #region 7. Grapple Gallore Mechanics
  [ExportGroup("10. Grapple Gallore Mechanics")]
  [Export] public float GrappleMaxDistance = 500.0f;
  [Export] public float GrappleStiffness = 30000.0f;
  [Export] public float GrappleDamping = 2000.0f;
  [Export] public float GrappleReelSpeed = 25.0f;
  [Export] public float SwingInfluenceForce = 1200.0f;

  [Export] public float SlingshotBoostMultiplier = 2.5f;
  [Export] public float ReelWhipForce = 8000.0f;
  #endregion

  #region 8. Flight Dynamics
  [ExportGroup("11. Flight Dynamics")]
  [Export] public bool FlightModeActive = false;
  [Export] public float MaxThrustPerEffector = 12000.0f;
  [Export] public float FlightSpeed = 50.0f;
  [Export] public float FlightAgility = 0.5f;
  [Export] public float FlightHeadPitchGazeBias = 5.9f;
  [Export] public float FlightHeadStiffnessOverride = 20000.0f;
  [Export] public float FlightHeadDampingOverride = 1500.0f;
  [Export] public float FlightStabilizationStiffness = 90000.0f;
  [Export] public float FlightStabilizationDamping = 5000.0f;
  #endregion

  #region Telemetry Configuration
  [ExportGroup("TELEMETRY")]
  [Export] public bool EnableTelemetry = true;
  [Export] public float TelemetryPrintRateHz = 10.0f;

  private UdpClient? _udpClient;
  private const string UDP_IP = "127.0.0.1";
  private const int UDP_PORT = 9870;

  private float _telemetryTimer = 0.0f;
  private float _lastUdpErrorTime = -10.0f;
  private const float ERROR_LOG_INTERVAL_SEC = 2.0f;

  // Cached metrics for emission
  private Vector3 _lastFlightForce = Vector3.Zero;
  #endregion

  // --- INTERNAL STATE ---
  private bool _isActive = true;
  private bool _wasFallen = false;
  private bool _wasFlightModeActive = false;
  private int _frameCounter = 0;
  private Vector3 _targetFlightVelocity = Vector3.Zero;
  private float _boostMultiplier = 1.0f;

  // --- DUAL GRAPPLE STATE ---
  private bool _isReeling = false;

  private bool _isGrapplingL = false;
  private List<Vector3> _grapplePathL = new List<Vector3>(8);
  private float _cableLengthL = 0.0f;
  private MeshInstance3D _cableVisualL = null!;

  private bool _isGrapplingR = false;
  private List<Vector3> _grapplePathR = new List<Vector3>(8);
  private float _cableLengthR = 0.0f;
  private MeshInstance3D _cableVisualR = null!;

  // --- PRE-ALLOCATED DATA STRUCTURES ---
  private List<MuscleGroup> _muscles = new List<MuscleGroup>(32);
  private List<LimbChain> _legs = new List<LimbChain>(2);
  private List<LimbChain> _groundedLegsBuffer = new List<LimbChain>(2);
  private Dictionary<PhysicalBone3D, ShaderMaterial> _plumeMaterials = new Dictionary<PhysicalBone3D, ShaderMaterial>();
  private readonly HashSet<int> _activeStanceBones = new HashSet<int>(32);
  private QuaternionAttitudeController _attitudeController = null!;

  // --- DEBUG RENDERERS ---
  private ImmediateMesh _gizmoMesh = new ImmediateMesh();
  private MeshInstance3D _gizmoInstance = new MeshInstance3D();

  // --- CAMERA STATE ---
  private Camera3D? _camera;
  private Node3D? _cameraPivot;
  private float _pitch = 0.0f;
  private float _yaw = 0.0f;
  private Control? _crosshairUI;
  private float _cameraStressShake = 0.0f;
  private RandomNumberGenerator _rng = new RandomNumberGenerator();

  // --- EFFECTS ---
  private GpuParticles3D _sparkEmitter = null!;

  private const string FLUID_PLUME_SHADER = @"
    shader_type spatial;
  render_mode blend_add, unshaded, cull_disabled, depth_draw_never;
  uniform float thrust_ratio = 0.0;
  float hash(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }
  float noise(vec2 p) {
    vec2 i = floor(p); vec2 f = fract(p); f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i + vec2(0.0, 0.0)), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
  }
  void vertex() {
    float expansion_factor = pow(UV.y, 2.0);
    float flutter = noise(vec2(VERTEX.y * 20.0 - TIME * 30.0, TIME * 15.0));
    vec3 push_dir = normalize(vec3(NORMAL.x, 0.0, NORMAL.z));
    VERTEX += push_dir * (flutter * 0.2 * expansion_factor * thrust_ratio);
    VERTEX.y *= (0.1 + thrust_ratio * 0.9);
  }
  void fragment() {
    float fire_mask = smoothstep(0.3, 0.6, (noise(vec2(UV.x * 5.0, UV.y * 4.0 - TIME * 14.0)) * 0.5 + noise(vec2(UV.x * 8.0 + TIME * 2.0, UV.y * 7.0 - TIME * 22.0)) * 0.5) - (UV.y * 0.7));
    ALBEDO = mix(mix(vec3(0.0, 0.5, 1.0), vec3(0.8, 0.9, 1.0), fire_mask), vec3(0.4, 0.0, 0.8), 1.0 - pow(UV.y, 1.5)) * (1.5 + fire_mask * 4.0);
    ALPHA = fire_mask * smoothstep(0.0, 0.4, max(dot(VIEW, NORMAL), 0.0)) * (1.0 - pow(UV.y, 2.0)) * smoothstep(0.01, 0.05, thrust_ratio);
  }
  ";

  private class MuscleGroup
  {
    public PhysicalBone3D Bone = null!;
    public PhysicalBone3D? ParentBone = null;
    public int BoneId;
    public bool IsSpine, IsLeg, IsHead, IsArm, IsRightArm, IsLeftArm, IsFinger;
    public List<MuscleGroup> ChildMuscles = new List<MuscleGroup>();

    public float SubtreeMass;
    public Vector3 InitialLocalSubtreeCOM;
    public Vector3 IntegralError = Vector3.Zero;
  }

  private class LimbChain
  {
    public string Name = "Leg";
    public PhysicalBone3D Foot = null!;
    public PhysicalBone3D LowerLeg = null!;
    public PhysicalBone3D UpperLeg = null!;
    public ShapeCast3D GroundSensor = null!;
    public List<int> ChainBoneIds = new List<int>();
  }

  public override void _Ready()
  {
    _rng.Randomize();
    Engine.PhysicsTicksPerSecond = 120;

    // --- NEW: Physics Engine Hardening ---
    // Force the physics server to use 64 iterations instead of the default 16.
    // This stops 6DOF joints from stretching and separating under heavy tension.
    PhysicsServer3D.SpaceSetParam(GetWorld3D().Space, PhysicsServer3D.SpaceParameter.SolverIterations, 64);

    _attitudeController = new QuaternionAttitudeController(FlightStabilizationStiffness, FlightStabilizationDamping);

    GenerateProceduralCity();
    SetupDebugGizmos();
    SetupFPSCamera();
    SetupCableVisuals();
    SetupSparkEmitter();

    if (TargetSimulatorPath != null && !TargetSimulatorPath.IsEmpty)
      _sim = GetNodeOrNull<PhysicalBoneSimulator3D>(TargetSimulatorPath);
    if (_sim == null) _sim = GetChildren().OfType<PhysicalBoneSimulator3D>().FirstOrDefault();

    if (_sim == null || AnimationShadow == null) {
      GD.PrintErr($"[CRITICAL] Missing Simulator or Shadow.");
      SetPhysicsProcess(false);
      return;
    }

    InitializeBones();
    PrecomputeSubtreeProperties();
    InitializeFlightEffectors();

    CallDeferred(nameof(StartPhysics));

    if (EnableTelemetry)
    {
        try
        {
            _udpClient = new UdpClient();
        }
        catch (System.Exception ex)
        {
            GD.PrintErr($"[TELEMETRY INIT ERROR] Failed to instantiate UdpClient: {ex.Message}");
        }
    }
  }

  public override void _ExitTree()
  {
    _udpClient?.Close();
    _udpClient?.Dispose();
    _udpClient = null;
  }

  private void InitializeBones()
  {
    foreach (var child in _sim!.GetChildren())
    {
      if (child is not PhysicalBone3D pb) continue;
      string boneName = pb.Get("bone_name").AsString();
      int bid = AnimationShadow.FindBone(boneName);

      if (bid == -1)
      {
        pb.QueueFree();
        continue;
      }

      string nameLower = boneName.ToLower();
      bool isFinger = nameLower.Contains("thumb") || nameLower.Contains("index") ||
        nameLower.Contains("middle") || nameLower.Contains("ring") ||
        nameLower.Contains("pinky");

      if (isFinger)
      {
        pb.QueueFree();
        continue;
      }

      pb.GravityScale = 1.0f;
      pb.CanSleep = false;
      pb.LinearDamp = 0.1f;
      pb.AngularDamp = 1.0f;

      pb.CollisionLayer = PlayerLayer;
      pb.CollisionMask = GroundMask;

      bool isArm = nameLower.Contains("arm") || nameLower.Contains("hand") || nameLower.Contains("shoulder") || nameLower.Contains("elbow");
      bool isRightArm = isArm && nameLower.Contains("right");
      bool isLeftArm = isArm && nameLower.Contains("left");

      MuscleGroup m = new MuscleGroup {
        Bone = pb,
        BoneId = bid,
        IsSpine = nameLower.Contains("spine") || nameLower.Contains("chest") || nameLower.Contains("torso") || nameLower.Contains("pelvis"),
        IsLeg = nameLower.Contains("leg") || nameLower.Contains("calf") || nameLower.Contains("foot") || nameLower.Contains("toe"),
        IsHead = nameLower.Contains("head") || nameLower.Contains("neck"),
        IsArm = isArm,
        IsRightArm = isRightArm,
        IsLeftArm = isLeftArm,
        IsFinger = false
      };

      _muscles.Add(m);

      if (pb.Name.ToString().Contains("Hips") || pb.Name.ToString().Contains("Pelvis")) _hips = pb;
      if (nameLower.Contains("spine2") || nameLower.Contains("chest") || nameLower.Contains("torso")) _chest = pb;

      if (isRightArm && nameLower.Contains("hand")) _grappleHandR = pb;
      if (isLeftArm && nameLower.Contains("hand")) _grappleHandL = pb;
    }

    if (_grappleHandR == null) _grappleHandR = _muscles.LastOrDefault(m => m.IsRightArm)?.Bone;
    if (_grappleHandL == null) _grappleHandL = _muscles.LastOrDefault(m => m.IsLeftArm)?.Bone;

    foreach (var m in _muscles) {
      int parentId = AnimationShadow.GetBoneParent(m.BoneId);
      if (parentId >= 0) {
        var parentMuscle = _muscles.FirstOrDefault(pm => pm.BoneId == parentId);
        if (parentMuscle != null) {
          m.ParentBone = parentMuscle.Bone;
          parentMuscle.ChildMuscles.Add(m);
        }
      }
    }

    if (_hips != null)
    {
      _hipMuscle = _muscles.FirstOrDefault(m => m.Bone == _hips);
    }
    if (_chest == null) _chest = _muscles.LastOrDefault(m => m.IsSpine && m.Bone != _hips)?.Bone;

    AutoConfigureMasses();
    ConfigureJoints();

    BuildLeg("Left", "LeftFoot", "LeftLeg", "LeftUpLeg");
    BuildLeg("Right", "RightFoot", "RightLeg", "RightUpLeg");
  }

  private void InitializeFlightEffectors()
  {
    _head = FindBonePhys("Head") ?? FindBonePhys("head") ?? FindBonePhys("Neck");
    _leftFoot = FindBonePhys("LeftFoot") ?? FindBonePhys("left_foot");
    _rightFoot = FindBonePhys("RightFoot") ?? FindBonePhys("right_foot");
    _leftUpLeg = FindBonePhys("LeftUpLeg") ?? FindBonePhys("left_up_leg");
    _rightUpLeg = FindBonePhys("RightUpLeg") ?? FindBonePhys("right_up_leg");
    _leftArm = FindBonePhys("LeftArm") ?? FindBonePhys("left_arm") ?? FindBonePhys("LeftShoulder");
    _rightArm = FindBonePhys("RightArm") ?? FindBonePhys("right_arm") ?? FindBonePhys("RightShoulder");

    AttachPlume(_grappleHandL, 0.3f * 0.08f, 0.4f);
    AttachPlume(_grappleHandR, 0.3f * 0.08f, 0.4f);
    AttachPlume(_leftFoot, 0.3f * 0.12f, 0.5f);
    AttachPlume(_rightFoot, 0.3f * 0.12f, 0.5f);
  }

  private void AttachPlume(PhysicalBone3D? bone, float radius, float length, Vector3? rotationOffset = null)
  {
    if (bone == null) return;
    var meshInst = new MeshInstance3D();
    var cyl = new CylinderMesh { TopRadius = 0.5f * radius, BottomRadius = radius * 0.1f, Height = 2.0f * length, RadialSegments = 16, Rings = 8 };
    meshInst.Mesh = cyl;
    var mat = new ShaderMaterial { Shader = new Shader { Code = FLUID_PLUME_SHADER } };
    mat.SetShaderParameter("thrust_ratio", 0.0f);
    meshInst.MaterialOverride = mat;
    if (rotationOffset.HasValue) meshInst.Rotation = rotationOffset.Value;
    meshInst.Position = new Vector3(0, length * -0.35f, 0);
    bone.AddChild(meshInst);
    _plumeMaterials[bone] = mat;

    var thrustLight = new OmniLight3D {
      LightColor = new Color(0.3f, 0.7f, 1.0f), LightEnergy = 0.0f, OmniRange = 15.0f, ShadowEnabled = true
    };
    meshInst.AddChild(thrustLight);
  }

  private void SetupSparkEmitter()
  {
    var sparkProcessMat = new ParticleProcessMaterial {
      Direction = new Vector3(0, 1, 0), Spread = 90, InitialVelocityMin = 5f, InitialVelocityMax = 12f,
      Gravity = new Vector3(0, -9.8f, 0), Color = Colors.Yellow
    };
    _sparkEmitter = new GpuParticles3D {
      ProcessMaterial = sparkProcessMat,
      DrawPass1 = new QuadMesh { Material = new StandardMaterial3D { EmissionEnabled = true, Emission = Colors.Yellow, VertexColorUseAsAlbedo = true }, Size = new Vector2(0.05f, 0.05f) },
      Emitting = false, OneShot = true, Explosiveness = 1.0f, Amount = 32
    };
    AddChild(_sparkEmitter);
  }

  private void PrecomputeSubtreeProperties()
  {
    foreach (var m in _muscles) CalculateSubtreePropertiesRecursive(m);
  }

  private (float mass, Vector3 weightedPos) CalculateSubtreePropertiesRecursive(MuscleGroup m)
  {
    float totalMass = m.Bone.Mass;
    Vector3 totalWeightedPos = m.Bone.Transform.Origin * m.Bone.Mass;

    foreach (var child in m.ChildMuscles)
    {
      var childProps = CalculateSubtreePropertiesRecursive(child);
      totalMass += childProps.mass;
      Vector3 childPosInLocal = m.Bone.Transform.Basis * childProps.weightedPos + m.Bone.Transform.Origin;
      totalWeightedPos += childPosInLocal * childProps.mass;
    }

    m.SubtreeMass = totalMass;
    m.InitialLocalSubtreeCOM = totalMass > 0 ? totalWeightedPos / totalMass : Vector3.Zero;
    return (totalMass, totalWeightedPos);
  }

  private void SetupCableVisuals()
  {
    var material = new StandardMaterial3D {
      AlbedoColor = Colors.White,
      ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded
    };

    _cableVisualL = new MeshInstance3D {
      Mesh = new ImmediateMesh(),
      MaterialOverride = material,
      Visible = false
    };

    _cableVisualR = new MeshInstance3D {
      Mesh = new ImmediateMesh(),
      MaterialOverride = material,
      Visible = false
    };

    GetTree().Root.CallDeferred("add_child", _cableVisualL);
    GetTree().Root.CallDeferred("add_child", _cableVisualR);
  }

  private void GenerateProceduralCity()
  {
    Node3D cityRoot = new Node3D { Name = "ProceduralCity" };
    GetTree().Root.CallDeferred("add_child", cityRoot);

    DirectionalLight3D sun = new DirectionalLight3D { RotationDegrees = new Vector3(-45, 45, 0), ShadowEnabled = true };
    cityRoot.AddChild(sun);

    StaticBody3D ground = new StaticBody3D { CollisionLayer = GroundMask, CollisionMask = GroundMask, Position = new Vector3(0, -1, 0) };
    ground.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(1000, 2, 1000) } });
    ground.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1000, 2, 1000) }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.1f, 0.1f, 0.15f) } });
    cityRoot.AddChild(ground);

    int gridSize = 15; float spacing = 45.0f;
    for (int x = -gridSize; x <= gridSize; x++)
    {
      for (int z = -gridSize; z <= gridSize; z++)
      {
        if (Mathf.Abs(x) < 2 && Mathf.Abs(z) < 2) continue;
        float height = _rng.RandfRange(50.0f, 250.0f), width = _rng.RandfRange(15.0f, 35.0f), depth = _rng.RandfRange(15.0f, 35.0f);
        StaticBody3D building = new StaticBody3D { Position = new Vector3(x * spacing, height / 2.0f, z * spacing), CollisionLayer = GroundMask, CollisionMask = GroundMask };
        building.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(width, height, depth) } });

        var mat = new StandardMaterial3D { Roughness = 0.8f };
        float shade = _rng.RandfRange(0.2f, 0.5f);
        mat.AlbedoColor = new Color(shade, shade + _rng.RandfRange(0, 0.1f), shade + _rng.RandfRange(0, 0.15f));
        building.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(width, height, depth) }, MaterialOverride = mat });
        cityRoot.AddChild(building);
      }
    }
  }

  private void SetupFPSCamera()
  {
    _cameraPivot = new Node3D { Name = "FPS_Cam_Pivot" };
    GetTree().Root.CallDeferred("add_child", _cameraPivot);
    _camera = new Camera3D { Name = "FPS_Camera", Far = 5000.0f };
    _cameraPivot.AddChild(_camera);
    CallDeferred(nameof(FinalizeCameraSetup));
  }

  private void FinalizeCameraSetup()
  {
    if (_cameraPivot == null || _camera == null) return;
    _camera.Position = new Vector3(0, 1.5f, 4.5f);
    _camera.Current = true;
    Input.MouseMode = Input.MouseModeEnum.Captured;
    CreateCrosshair();
  }

  private void CreateCrosshair()
  {
    _crosshairUI = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
    _crosshairUI.SetAnchorsPreset(Control.LayoutPreset.FullRect);
    var centerPoint = new ColorRect { Color = Colors.White, CustomMinimumSize = new Vector2(4, 4) };
    centerPoint.SetAnchorsPreset(Control.LayoutPreset.Center);
    _crosshairUI.AddChild(centerPoint);
    var canvas = new CanvasLayer();
    canvas.AddChild(_crosshairUI);
    GetTree().Root.CallDeferred("add_child", canvas);
  }

  private void ConfigureJoints()
  {
    foreach (var m in _muscles)
    {
      if (m.Bone == _hips) continue;
      m.Bone.JointType = PhysicalBone3D.JointTypeEnum.Type6Dof;
      float limitX = 45.0f, limitY = 45.0f, limitZ = 45.0f;

      if (m.IsSpine) { limitX = 30.0f; limitY = 2.0f; limitZ = 10.0f; }
      else if (m.IsHead) { limitX = 30.0f; limitY = 30.0f; limitZ = 30.0f; }
      else if (m.IsLeg) { limitX = 90.0f; limitY = 5.0f; limitZ = 10.0f; }
      else if (m.IsArm) {
        string n = m.Bone.Name.ToString().ToLower();
        if (n.Contains("forearm") || n.Contains("lower")) { limitX = 90.0f; limitY = 5.0f; limitZ = 5.0f; }
        else { limitX = 120.0f; limitY = 120.0f; limitZ = 120.0f; }
      }

      m.Bone.Set("joint_constraints/angular_limit_x/enabled", true);
      m.Bone.Set("joint_constraints/angular_limit_x/upper_angle", Mathf.DegToRad(limitX));
      m.Bone.Set("joint_constraints/angular_limit_x/lower_angle", Mathf.DegToRad(-limitX));
      m.Bone.Set("joint_constraints/angular_limit_y/enabled", true);
      m.Bone.Set("joint_constraints/angular_limit_y/upper_angle", Mathf.DegToRad(limitY));
      m.Bone.Set("joint_constraints/angular_limit_y/lower_angle", Mathf.DegToRad(-limitY));
      m.Bone.Set("joint_constraints/angular_limit_z/enabled", true);
      m.Bone.Set("joint_constraints/angular_limit_z/upper_angle", Mathf.DegToRad(limitZ));
      m.Bone.Set("joint_constraints/angular_limit_z/lower_angle", Mathf.DegToRad(-limitZ));
    }
  }

  private void AutoConfigureMasses()
  {
    foreach (var m in _muscles) {
      if (m.IsHead) m.Bone.Mass = 3.0f;
      else if (m.IsArm) m.Bone.Mass = 1.5f;
      else if (m.IsSpine) m.Bone.Mass = 4.0f;
      else if (m.Bone == _hips) m.Bone.Mass = 12.0f;
      else if (m.IsLeg) m.Bone.Mass = m.Bone.Name.ToString().ToLower().Contains("up") ? 6.0f : 4.0f;
      else m.Bone.Mass = 1.0f;
    }
  }

  private void BuildLeg(string prefix, string footName, string lowName, string upName)
  {
    var foot = FindBonePhys(footName);
    var low = FindBonePhys(lowName);
    var up = FindBonePhys(upName);

    if (foot != null && low != null && up != null) {
      PhysicsServer3D.BodySetParam(foot.GetRid(), PhysicsServer3D.BodyParameter.Friction, 1.0f);
      var sensor = new ShapeCast3D { Shape = new SphereShape3D { Radius = 0.15f }, TargetPosition = Vector3.Down * 0.5f, CollisionMask = GroundMask, ExcludeParent = true };
      foreach(var muscle in _muscles) sensor.AddException(muscle.Bone);
      foot.AddChild(sensor);

      var chain = new LimbChain { Name = prefix, Foot = foot, LowerLeg = low, UpperLeg = up, GroundSensor = sensor };
      chain.ChainBoneIds.Add(AnimationShadow.FindBone(upName));
      chain.ChainBoneIds.Add(AnimationShadow.FindBone(lowName));
      chain.ChainBoneIds.Add(AnimationShadow.FindBone(footName));
      _legs.Add(chain);
    }
  }

  private void StartPhysics() { if (_sim != null) { _sim.Active = true; _sim.PhysicalBonesStartSimulation(); } }

  public override void _Input(InputEvent @event) {
    if (@event.IsActionPressed("ui_cancel"))
      Input.MouseMode = (Input.MouseMode == Input.MouseModeEnum.Captured) ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;

    if (Input.MouseMode != Input.MouseModeEnum.Captured) return;

    if (@event is InputEventMouseMotion m) {
      _yaw -= m.Relative.X * MouseSensitivity;
      _pitch -= m.Relative.Y * MouseSensitivity;
      _pitch = Mathf.Clamp(_pitch, -Mathf.Pi / 2.0f + 0.1f, Mathf.Pi / 2.0f - 0.1f);
      if (_cameraPivot != null) {
        _cameraPivot.Rotation = new Vector3(0, _yaw, 0);
        if (_camera != null) _camera.Rotation = new Vector3(_pitch, 0, 0);
      }
    }

    if (@event is InputEventKey fKey && fKey.Pressed && fKey.Keycode == Key.F)
    {
      FlightModeActive = !FlightModeActive;
      _targetFlightVelocity = Vector3.Zero;
      for(int i = 0; i < _muscles.Count; i++) _muscles[i].IntegralError = Vector3.Zero;
    }

    if (@event is InputEventMouseButton grabMb)
    {
      if (grabMb.ButtonIndex == MouseButton.Left)
      {
        if (grabMb.Pressed) EngageGrapple(isLeft: true);
        else ReleaseGrapple(isLeft: true);
      }

      if (grabMb.ButtonIndex == MouseButton.Right)
      {
        if (grabMb.Pressed) EngageGrapple(isLeft: false);
        else ReleaseGrapple(isLeft: false);
      }
    }

    if (@event is InputEventKey keyEvent && keyEvent.Keycode == Key.E)
    {
      _isReeling = keyEvent.Pressed;
    }
  }

  public override void _Process(double delta)
  {
    if (_cameraPivot == null || _camera == null || Input.MouseMode != Input.MouseModeEnum.Captured) return;

    if (_chest != null)
    {
      _cameraPivot.GlobalPosition = _cameraPivot.GlobalPosition.Lerp(_chest.GlobalPosition, (float)delta * 25.0f);

      if (FlightModeActive)
      {
        float angularStress = _chest.AngularVelocity.LengthSquared() * 0.005f;
        _cameraStressShake = Mathf.Lerp(_cameraStressShake, Mathf.Clamp(angularStress, 0f, 1f), (float)delta * 10f);
        // disabled
        if (false && _cameraStressShake > 0.05f) {
          float shakeAmt = _cameraStressShake * 0.15f;
          _camera.HOffset = (GD.Randf() - 0.5f) * shakeAmt;
          _camera.VOffset = (GD.Randf() - 0.5f) * shakeAmt;
        } else { _camera.HOffset = 0f; _camera.VOffset = 0f; }
      }
      else { _camera.HOffset = 0f; _camera.VOffset = 0f; }
    }

    if (FlightModeActive && _hips != null)
    {
      float currentSpeed = _hips.LinearVelocity.Length();
      float targetFov = 75.0f + (currentSpeed * 0.4f);
      _camera.Fov = Mathf.Lerp(_camera.Fov, Mathf.Clamp(targetFov, 75.0f, 110.0f), (float)delta * 3.0f);
    }
    else
    {
      _camera.Fov = Mathf.Lerp(_camera.Fov, 75.0f, (float)delta * 5.0f);
    }

    UpdateCableVisual(_isGrapplingL, _grappleHandL, _grapplePathL, _cableVisualL);
    UpdateCableVisual(_isGrapplingR, _grappleHandR, _grapplePathR, _cableVisualR);
  }

  private void UpdateCableVisual(bool isGrappling, PhysicalBone3D? hand, List<Vector3> path, MeshInstance3D visual)
  {
    if (isGrappling && hand != null && visual != null && path.Count > 0)
    {
      visual.Visible = true;
      visual.GlobalPosition = Vector3.Zero;
      visual.GlobalBasis = Basis.Identity;

      if (visual.Mesh is ImmediateMesh mesh)
      {
        mesh.ClearSurfaces();
        mesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

        mesh.SurfaceAddVertex(hand.GlobalPosition);
        mesh.SurfaceAddVertex(path[path.Count - 1]);

        for (int i = path.Count - 1; i > 0; i--)
        {
          mesh.SurfaceAddVertex(path[i]);
          mesh.SurfaceAddVertex(path[i - 1]);
        }

        mesh.SurfaceEnd();
      }
    }
    else if (visual != null) visual.Visible = false;
  }

  public override void _PhysicsProcess(double delta)
  {
    if (!_isActive || _sim == null || !_sim.Active || _hips == null) return;
    float dt = (float)delta;
    if (dt <= 0f) return;

    for (int i = 0; i < _muscles.Count; i++)
    {
      // --- NEW: Linear Velocity Hard Clamp ---
      // Prevents infinite energy spikes from cascading up the skeleton and ripping joints apart
      if (_muscles[i].Bone.LinearVelocity.LengthSquared() > 10000.0f) // > 100 m/s
      {
        _muscles[i].Bone.LinearVelocity = _muscles[i].Bone.LinearVelocity.Normalized() * 100.0f;
      }

      if (_muscles[i].Bone.AngularVelocity.LengthSquared() > 2500.0f)
      {
        _muscles[i].Bone.AngularVelocity = _muscles[i].Bone.AngularVelocity.Normalized() * 50.0f;
      }
    }

    _frameCounter++;
    bool isDebugFrame = EnableDebugLogs && (_frameCounter % 60 == 0);

    RagdollState state = ComputeKinematicState();
    _activeStanceBones.Clear();
    _groundedLegsBuffer.Clear();

    for (int i = 0; i < _legs.Count; i++)
    {
      var leg = _legs[i];
      if (leg.GroundSensor.IsColliding())
      {
        state.IsAirborne = false;
        _groundedLegsBuffer.Add(leg);
        for(int j = 0; j < leg.ChainBoneIds.Count; j++) _activeStanceBones.Add(leg.ChainBoneIds[j]);

        Vector3 footVel = leg.Foot.LinearVelocity;
        if (footVel.LengthSquared() > 100.0f && !_sparkEmitter.Emitting)
        {
          _sparkEmitter.GlobalPosition = leg.GroundSensor.GetCollisionPoint(0);
          _sparkEmitter.Restart();
        }
      }
    }

    state.IsFallen = _hips.GlobalPosition.Y < FallenHeight;
    bool isAnyGrappling = _isGrapplingL || _isGrapplingR;

    if (FlightModeActive)
    {
      _wasFlightModeActive = true;
      HandleFlightInput(dt);

      Basis targetBasis = ComputeTargetFlightBasis(dt);
      AnimationShadow.GlobalBasis = targetBasis;
      AnimationShadow.GlobalPosition = _chest?.GlobalPosition ?? Vector3.Zero;

      float altitude = _chest?.GlobalPosition.Y ?? 0f;
      float turbulenceIntensity = Mathf.Clamp((altitude - 100.0f) / 200.0f, 0.0f, 1.0f);
      if (turbulenceIntensity > 0.01f)
      {
        float time = Time.GetTicksMsec() * 0.001f;
        float maxWindSpeed = 2.5f;
        _ambientWind = new Vector3(
            (float)Mathf.Sin(time * 4.0) * maxWindSpeed,
            (float)Mathf.Cos(time * 5.3) * (maxWindSpeed * 0.3f),
            (float)Mathf.Sin(time * 4.7) * maxWindSpeed
            ) * turbulenceIntensity;
      }
      else _ambientWind = Vector3.Zero;

      ApplyAerodynamicDrag(dt);

      if (isAnyGrappling) {
        if (_isGrapplingL) ProcessCableWrapping(_grappleHandL, _grapplePathL);
        if (_isGrapplingR) ProcessCableWrapping(_grappleHandR, _grapplePathR);
        EvaluateGrappleConstraint(_isGrapplingL, _grappleHandL, _grapplePathL, ref _cableLengthL, dt);
        EvaluateGrappleConstraint(_isGrapplingR, _grappleHandR, _grapplePathR, ref _cableLengthR, dt);
      }

      ApplyPoseMatching(dt, false, 2.5f, _activeStanceBones);
      ApplyGravityCompensation(dt);

      Vector3 flightForce = ComputeFlightForce(ref state, dt);
      _lastFlightForce = flightForce;
      ApplyCoupledFlightDynamics(dt, targetBasis, flightForce);

      if (DrawDebugGizmos) DrawGizmos(ref state);
      if (EnableTelemetry)
      {
        _telemetryTimer += (float)delta;
        if (TelemetryPrintRateHz > 0 && _telemetryTimer >= (1.0f / TelemetryPrintRateHz))
        {
          LogTelemetry(state);
          _telemetryTimer = 0.0f;
        }
      }

      return;
    }

    if (_wasFlightModeActive)
    {
      AnimationShadow.GlobalBasis = Basis.Identity;
      AnimationShadow.GlobalPosition = Vector3.Zero;
      foreach (var mat in _plumeMaterials.Values) mat.SetShaderParameter("thrust_ratio", 0.0f);
      _wasFlightModeActive = false;
      for(int i = 0; i < _muscles.Count; i++) _muscles[i].IntegralError = Vector3.Zero;
    }

    if (!state.IsFallen && _wasFallen && !state.IsAirborne)
    {
      for(int i = 0; i < _muscles.Count; i++) _muscles[i].IntegralError = Vector3.Zero;
    }
    _wasFallen = state.IsFallen;

    if (isAnyGrappling)
    {
      ApplyAerodynamicDrag(dt);
      ApplySwingInfluence(dt);
      if (_isGrapplingL) ProcessCableWrapping(_grappleHandL, _grapplePathL);
      if (_isGrapplingR) ProcessCableWrapping(_grappleHandR, _grapplePathR);
      EvaluateGrappleConstraint(_isGrapplingL, _grappleHandL, _grapplePathL, ref _cableLengthL, dt);
      EvaluateGrappleConstraint(_isGrapplingR, _grappleHandR, _grapplePathR, ref _cableLengthR, dt);
      ApplyPoseMatching(dt, false, 1.0f, _activeStanceBones);
      ApplyGravityCompensation(dt);
    }
    else if (state.IsAirborne)
    {
      ApplyAerodynamicDrag(dt);
      ApplySwingInfluence(dt);
      ApplyPoseMatching(dt, false, 0.05f, _activeStanceBones);
      ApplyGravityCompensation(dt);
    }
    else if (state.IsFallen)
    {
      ApplyHipRecovery(dt);
      ApplyPoseMatching(dt, false, 0.5f, _activeStanceBones);
      ApplyVirtualModelControl(ref state, dt);
    }
    else
    {
      ApplySpinalExtension(dt);
      ApplyGravityCompensation(dt);
      ApplyPoseMatching(dt, isDebugFrame, 1.0f, _activeStanceBones);
      ApplyCoreStabilization(ref state, dt);
      ApplyVirtualModelControl(ref state, dt);
      ApplyAnkleStrategy(dt);
    }

    if (DrawDebugGizmos) DrawGizmos(ref state);
    if (EnableTelemetry)
    {
        _telemetryTimer += (float)delta;
        if (TelemetryPrintRateHz > 0 && _telemetryTimer >= (1.0f / TelemetryPrintRateHz))
        {
            LogTelemetry(state);
            _telemetryTimer = 0.0f;
        }
    }
  }

  private void LogTelemetry(RagdollState state)
  {
    if (_udpClient == null)
    {
      ReportTelemetryError("UdpClient is uninitialized or null.");
      return;
    }

    Vector3 coreVel = (_hips != null) ? _hips.LinearVelocity : state.Velocity;

    var metrics = new
    {
      timestamp = Time.GetTicksMsec() / 1000.0f,

      // Kinematics & Flight State
      flight_mode = FlightModeActive ? 1 : 0,
      alt_actual = _chest?.GlobalPosition.Y ?? state.CenterOfMass.Y,
      com_y = state.CenterOfMass.Y,
      vel_x = coreVel.X,
      vel_y = coreVel.Y,
      vel_z = coreVel.Z,
      target_vel_mag = _targetFlightVelocity.Length(),
      actual_vel_mag = coreVel.Length(),
      flight_force_n = _lastFlightForce.Length(),

      // Attitude & Turbulence Metrics
      chest_ang_vel_mag = _chest?.AngularVelocity.Length() ?? 0.0f,

      // Grapple Mechanics
      is_grappling_l = _isGrapplingL ? 1 : 0,
      cable_length_l = _cableLengthL,
      is_grappling_r = _isGrapplingR ? 1 : 0,
      cable_length_r = _cableLengthR,
      is_reeling = _isReeling ? 1 : 0,

      // Ragdoll Flags
      is_airborne = state.IsAirborne ? 1 : 0,
      is_fallen = state.IsFallen ? 1 : 0
    };

    try
    {
      string jsonString = JsonSerializer.Serialize(metrics);
      byte[] payload = Encoding.UTF8.GetBytes(jsonString);

      _udpClient.Send(payload, payload.Length, UDP_IP, UDP_PORT);
    }
    catch (SocketException ex)
    {
      ReportTelemetryError($"SocketException on port {UDP_PORT}: {ex.Message} (Code: {ex.SocketErrorCode})");
    }
    catch (System.Exception ex)
    {
      ReportTelemetryError($"Unexpected telemetry error: {ex.Message}");
    }
  }

  private void ReportTelemetryError(string message)
  {
    float currentTime = Time.GetTicksMsec() / 1000.0f;
    if (currentTime - _lastUdpErrorTime >= ERROR_LOG_INTERVAL_SEC)
    {
      GD.PrintErr($"[TELEMETRY ERROR] {message}");
      _lastUdpErrorTime = currentTime;
    }
  }

  private RagdollState ComputeKinematicState()
  {
    RagdollState state = new RagdollState { IsAirborne = true, TotalMass = 0f };
    Vector3 weightedPos = Vector3.Zero;
    Vector3 weightedVel = Vector3.Zero;

    for (int i = 0; i < _muscles.Count; i++)
    {
      var m = _muscles[i];
      float mass = m.Bone.Mass;
      state.TotalMass += mass;
      weightedPos += m.Bone.GlobalPosition * mass;
      weightedVel += m.Bone.LinearVelocity * mass;
    }

    if (state.TotalMass > 0.001f)
    {
      state.CenterOfMass = weightedPos / state.TotalMass;
      state.Velocity = weightedVel / state.TotalMass;
    }
    else state.CenterOfMass = _hips?.GlobalPosition ?? Vector3.Zero;

    return state;
  }

  // ------------------------------------------------------------------
  // FLIGHT MECHANICS
  // ------------------------------------------------------------------
  private void HandleFlightInput(float dt)
  {
    if (_camera == null) return;
    Vector3 camForward = -_camera.GlobalBasis.Z.Normalized();
    Vector3 camRight = _camera.GlobalBasis.X.Normalized();
    Vector3 moveDir = Vector3.Zero;

    if (Input.IsPhysicalKeyPressed(Key.W)) moveDir += camForward;
    if (Input.IsPhysicalKeyPressed(Key.S)) moveDir -= camForward;
    if (Input.IsPhysicalKeyPressed(Key.A)) moveDir -= camRight;
    if (Input.IsPhysicalKeyPressed(Key.D)) moveDir += camRight;
    if (Input.IsPhysicalKeyPressed(Key.Space)) moveDir += Vector3.Up;
    if (Input.IsPhysicalKeyPressed(Key.Shift)) moveDir += Vector3.Down;
    _boostMultiplier = Input.IsPhysicalKeyPressed(Key.Alt) ? 3.0f : 1.0f;

    if (moveDir.LengthSquared() > 0.01f)
      _targetFlightVelocity = _targetFlightVelocity.Lerp(moveDir.Normalized() * FlightSpeed * _boostMultiplier, FlightAgility * dt);
    else
      _targetFlightVelocity = _targetFlightVelocity.Lerp(Vector3.Zero, (FlightAgility * 0.5f) * dt);
  }

  private Basis ComputeTargetFlightBasis(float dt)
  {
    if (_camera == null) return Basis.Identity;
    Vector3 camForwardFlat = -_camera.GlobalBasis.Z;
    camForwardFlat.Y = 0;
    if (camForwardFlat.LengthSquared() < 0.001f) camForwardFlat = -_camera.GlobalBasis.Y;

    Basis hoverBasis = Basis.LookingAt(camForwardFlat.Normalized(), Vector3.Up);
    float speed = _targetFlightVelocity.Length();
    if (speed < 2.0f) return hoverBasis;

    Vector3 velDir = _targetFlightVelocity.Normalized();
    Vector3 desiredUp = velDir;
    Vector3 desiredFront = Vector3.Down;
    if (Mathf.Abs(desiredUp.Dot(desiredFront)) > 0.99f) desiredFront = camForwardFlat.Normalized();

    Basis cruiseBasis = Basis.LookingAt(desiredFront, desiredUp);
    float speedBlend = Mathf.Clamp((speed - 5.0f) / 15.0f, 0.0f, 1.0f);
    Basis finalBasis = new Basis(hoverBasis.GetRotationQuaternion().Slerp(cruiseBasis.GetRotationQuaternion(), speedBlend));

    float lateralInput = (Input.IsPhysicalKeyPressed(Key.A) ? -1f : 0f) + (Input.IsPhysicalKeyPressed(Key.D) ? 1f : 0f);
    Vector3 localVelocity = hoverBasis.Inverse() * _targetFlightVelocity;
    float driftBank = -localVelocity.X * 0.05f;
    float inputBank = lateralInput * -Mathf.Pi / 3.0f * speedBlend;
    float finalBankAngle = Mathf.Clamp(inputBank + driftBank, -1.2f, 1.2f);

    finalBasis = finalBasis.Rotated(finalBasis.Y, finalBankAngle);
    return finalBasis;
  }

  private Vector3 ComputeFlightForce(ref RagdollState state, float dt)
  {
    Vector3 coreVelocity = (_hips != null && _chest != null) ? (_hips.LinearVelocity * 0.6f + _chest.LinearVelocity * 0.4f) : state.Velocity;
    if (!coreVelocity.IsFinite()) coreVelocity = Vector3.Zero;
    Vector3 velocityError = _targetFlightVelocity - coreVelocity;

    float groundEffectMult = 1.0f;
    if (_chest != null)
    {
      var spaceState = GetWorld3D().DirectSpaceState;
      var query = PhysicsRayQueryParameters3D.Create(_chest.GlobalPosition, _chest.GlobalPosition + Vector3.Down * 8.0f);
      var result = spaceState.IntersectRay(query);
      if (result.Count > 0)
      {
        float distToGround = _chest.GlobalPosition.Y - (float)result["position"].AsVector3().Y;
        if (distToGround < 5.0f && coreVelocity.Y < -2.0f) {
          float brakingForce = Mathf.Abs(coreVelocity.Y) * (5.0f - distToGround);
          velocityError.Y += brakingForce * dt * 10.0f;
        }
        if (distToGround < 2.0f) groundEffectMult += 0.5f * Mathf.Exp(-distToGround * 2.0f);
      }
    }

    float descentFactor = _targetFlightVelocity.Y < -0.1f ? Mathf.Clamp(Mathf.Abs(_targetFlightVelocity.Y) / (FlightSpeed * _boostMultiplier), 0.0f, 1.0f) : 0.0f;
    Vector3 gravityComp = Vector3.Up * state.TotalMass * 9.81f * (1.0f - descentFactor);
    Vector3 maneuverForce = velocityError * state.TotalMass * (2.5f * _boostMultiplier * groundEffectMult);
    Vector3 desiredForce = maneuverForce + gravityComp;

    return desiredForce.IsFinite() ? desiredForce.LimitLength(MaxThrustPerEffector * 4.0f * _boostMultiplier) : Vector3.Zero;
  }

  private void ApplyCoupledFlightDynamics(float dt, Basis targetBasis, Vector3 desiredGlobalForce)
  {
    if (_grappleHandL == null || _grappleHandR == null || _leftFoot == null || _rightFoot == null || _chest == null || _hips == null) return;

    Vector3 desiredGlobalTorque = _attitudeController.ComputeTorque(_chest.GlobalBasis.GetRotationQuaternion(), targetBasis.GetRotationQuaternion(), _chest.AngularVelocity);
    desiredGlobalTorque = desiredGlobalTorque.LimitLength(FlightStabilizationStiffness * 0.5f);

    ApplyTorque(_chest, desiredGlobalTorque * 0.7f, dt);
    ApplyTorque(_hips, desiredGlobalTorque * 0.3f, dt);

    Vector3 coreForce = desiredGlobalForce * 0.7f;
    Vector3 extremityForce = (desiredGlobalForce * 0.3f) / 4.0f;

    ApplyForce(_chest, coreForce * 0.6f, dt);
    ApplyForce(_hips, coreForce * 0.4f, dt);
    PhysicalBone3D[] effectors = { _grappleHandL, _grappleHandR, _leftFoot, _rightFoot };
    for (int i = 0; i < effectors.Length; i++) ApplyForce(effectors[i], extremityForce, dt);

    if (desiredGlobalForce.LengthSquared() > 0.1f)
    {
      Vector3 thrustDir = desiredGlobalForce.Normalized();
      Vector3 braceDir = -thrustDir;
      float braceStiff = 4000.0f, braceDamp = 150.0f;

      if (_leftUpLeg != null) ApplyDirectionalTorque(_leftUpLeg, braceDir, dt, braceStiff, braceDamp);
      if (_rightUpLeg != null) ApplyDirectionalTorque(_rightUpLeg, braceDir, dt, braceStiff, braceDamp);
      if (_leftArm != null) ApplyDirectionalTorque(_leftArm, braceDir, dt, braceStiff * 0.8f, braceDamp * 0.8f);
      if (_rightArm != null) ApplyDirectionalTorque(_rightArm, braceDir, dt, braceStiff * 0.8f, braceDamp * 0.8f);
    }

    UpdatePlumeVisuals(desiredGlobalForce.Length());
  }

  private void UpdatePlumeVisuals(float totalGlobalForceMagnitude)
  {
    float absoluteMaxThrust = MaxThrustPerEffector * 4.0f * _boostMultiplier;
    float thrustRatio = Mathf.Clamp(totalGlobalForceMagnitude / absoluteMaxThrust, 0.0f, 1.0f);
    if (totalGlobalForceMagnitude > 0.01f && thrustRatio < 0.15f) thrustRatio = Mathf.Clamp(totalGlobalForceMagnitude / absoluteMaxThrust, 0.15f, 1.0f);
    float dynamicLengthScale = Mathf.Lerp(0.1f, 3.0f, thrustRatio);

    PhysicalBone3D?[] thrusterBones = { _grappleHandL, _grappleHandR, _leftFoot, _rightFoot };
    for (int i = 0; i < thrusterBones.Length; i++)
    {
      var bone = thrusterBones[i];
      if (bone != null && _plumeMaterials.TryGetValue(bone, out var mat))
      {
        mat.SetShaderParameter("thrust_ratio", thrustRatio);
        foreach(var child in bone.GetChildren())
        {
          if (child is OmniLight3D light) light.LightEnergy = thrustRatio * 5.0f;
          if (child is MeshInstance3D meshInst)
          {
            Vector3 currentScale = meshInst.Scale;
            currentScale.Y = dynamicLengthScale;
            meshInst.Scale = currentScale;
            meshInst.Position = (bone == _leftFoot || bone == _rightFoot)
              ? new Vector3(0, (0.5f * dynamicLengthScale) * -0.35f, 0)
              : new Vector3(0, (0.4f * dynamicLengthScale) * -0.35f, 0);
            break;
          }
        }
      }
    }
  }

  private void ApplyDirectionalTorque(PhysicalBone3D bone, Vector3 targetDirection, float dt, float stiffness, float damping)
  {
    Vector3 currentDir = bone.GlobalBasis.Y;
    if (currentDir.Dot(targetDirection) > 0.999f) return;
    Vector3 axis = currentDir.Cross(targetDirection);
    float angle = Mathf.Asin(Mathf.Clamp(axis.Length(), -1.0f, 1.0f));
    if (angle > 0.001f)
    {
      axis = axis.Normalized();
      ApplyTorque(bone, (axis * angle * stiffness) - (bone.AngularVelocity * damping), dt);
    }
  }

  // ------------------------------------------------------------------
  // GRAPPLE MECHANICS
  // ------------------------------------------------------------------
  private void EngageGrapple(bool isLeft)
  {
    if (_camera == null) return;
    PhysicalBone3D? hand = isLeft ? _grappleHandL : _grappleHandR;
    if (hand == null) return;

    var spaceState = GetWorld3D().DirectSpaceState;
    var query = PhysicsRayQueryParameters3D.Create(_camera.GlobalPosition, _camera.GlobalPosition - _camera.GlobalBasis.Z * GrappleMaxDistance);
    query.CollisionMask = GroundMask;

    var result = spaceState.IntersectRay(query);
    if (result.Count > 0)
    {
      Vector3 anchor = result["position"].AsVector3();
      Vector3 normal = result["normal"].AsVector3();

      // Keep the anchor slightly pushed out to avoid surface clipping
      anchor += normal * 0.1f;
      float length = hand.GlobalPosition.DistanceTo(anchor);

      if (isLeft) { _isGrapplingL = true; _grapplePathL.Clear(); _grapplePathL.Add(anchor); _cableLengthL = length; }
      else { _isGrapplingR = true; _grapplePathR.Clear(); _grapplePathR.Add(anchor); _cableLengthR = length; }

      if (_crosshairUI != null) _crosshairUI.RotationDegrees = 45;
    }
  }

  private void ReleaseGrapple(bool isLeft)
  {
    if (isLeft) { _isGrapplingL = false; _grapplePathL.Clear(); }
    else { _isGrapplingR = false; _grapplePathR.Clear(); }

    if (!_isGrapplingL && !_isGrapplingR)
    {
      if (_crosshairUI != null) _crosshairUI.RotationDegrees = 0;
      for(int i = 0; i < _muscles.Count; i++) _muscles[i].IntegralError = Vector3.Zero;
    }
  }

  private void ProcessCableWrapping(PhysicalBone3D hand, List<Vector3> path)
  {
    if (path.Count == 0) return;

    var spaceState = GetWorld3D().DirectSpaceState;
    Vector3 handPos = hand.GlobalPosition;
    Vector3 targetNode = path[path.Count - 1];

    // 1. CHECK UNWRAP FIRST. If we can cleanly see the previous node, unwrap and abort frame calculation.
    // This prevents an infinite flicker where it wraps and unwraps simultaneously.
    if (path.Count > 1)
    {
      Vector3 previousNode = path[path.Count - 2];
      var unwrapQuery = PhysicsRayQueryParameters3D.Create(handPos, previousNode);
      unwrapQuery.CollisionMask = GroundMask;

      var unwrapResult = spaceState.IntersectRay(unwrapQuery);

      if (unwrapResult.Count == 0)
      {
        path.RemoveAt(path.Count - 1);
        return;
      }
    }

    // 2. CHECK FOR NEW WRAPS.
    var wrapQuery = PhysicsRayQueryParameters3D.Create(handPos, targetNode);
    wrapQuery.CollisionMask = GroundMask;

    var wrapResult = spaceState.IntersectRay(wrapQuery);

    if (wrapResult.Count > 0)
    {
      Vector3 hitPoint = wrapResult["position"].AsVector3();
      // 3. TOLERANCE: Prevent micro-wrapping on perfectly flat walls.
      if (hitPoint.DistanceTo(targetNode) > 0.3f && hitPoint.DistanceTo(handPos) > 0.3f)
      {
        Vector3 normal = wrapResult["normal"].AsVector3();
        // Offset out slightly to avoid raycast surface clipping loops
        path.Add(hitPoint + normal * 0.1f);
      }
    }
  }

  private void ApplySwingInfluence(float dt)
  {
    if (_camera == null) return;

    float x = 0; float y = 0;
    if (Input.IsPhysicalKeyPressed(Key.W) || Input.IsActionPressed("ui_up")) y = -1;
    if (Input.IsPhysicalKeyPressed(Key.S) || Input.IsActionPressed("ui_down")) y = 1;
    if (Input.IsPhysicalKeyPressed(Key.A) || Input.IsActionPressed("ui_left")) x = -1;
    if (Input.IsPhysicalKeyPressed(Key.D) || Input.IsActionPressed("ui_right")) x = 1;

    Vector2 inputDir = new Vector2(x, y).Normalized();

    if (inputDir.LengthSquared() > 0.01f)
    {
      Vector3 camForward = -_camera.GlobalBasis.Z;
      camForward.Y = 0;
      camForward = camForward.Normalized();

      Vector3 camRight = _camera.GlobalBasis.X;
      camRight.Y = 0;
      camRight = camRight.Normalized();

      Vector3 inputForceDir = (camRight * inputDir.X + camForward * inputDir.Y);

      Vector3 avgVelocity = Vector3.Zero;
      float totalMass = _muscles.Sum(m => m.Bone.Mass);
      foreach (var m in _muscles) avgVelocity += m.Bone.LinearVelocity * (m.Bone.Mass / totalMass);

      float momentumAlignment = Mathf.Max(1.0f, inputForceDir.Dot(avgVelocity.Normalized()) * 3.0f + 1.0f);
      Vector3 swingForce = inputForceDir * SwingInfluenceForce * momentumAlignment;

      foreach (var m in _muscles)
      {
        Vector3 distForce = swingForce * (m.Bone.Mass / totalMass);
        ApplyForce(m.Bone, distForce, dt);
      }
    }
  }

  private void EvaluateGrappleConstraint(bool isGrappling, PhysicalBone3D? hand, List<Vector3> path, ref float length, float dt)
  {
    if (!isGrappling || hand == null || path.Count == 0) return;

    bool isReelingNow = _isReeling;
    if (isReelingNow) length = Mathf.Max(2.0f, length - (GrappleReelSpeed * dt));

    Vector3 currentAnchor = path[path.Count - 1];
    Vector3 handPos = hand.GlobalPosition;
    Vector3 cableVector = handPos - currentAnchor;
    float distToAnchor = cableVector.Length();

    if (distToAnchor > 0.1f)
    {
      Vector3 cableDir = cableVector.Normalized();

      float consumedLength = 0f;
      for (int i = 0; i < path.Count - 1; i++) consumedLength += path[i].DistanceTo(path[i + 1]);
      float availableLength = Mathf.Max(0.1f, length - consumedLength);

      float displacement = distToAnchor - availableLength;

      if (displacement > 0)
      {
        float velocityAlongCable = hand.LinearVelocity.Dot(cableDir);
        float tensionMag = -(GrappleStiffness * displacement) - (GrappleDamping * velocityAlongCable);

        if (tensionMag < 0)
        {
          float totalMass = _muscles.Sum(m => m.Bone.Mass);
          Vector3 totalForce = cableDir * tensionMag;

          Vector3 tangentVelocity = hand.LinearVelocity - (cableDir * velocityAlongCable);

          if (tangentVelocity.LengthSquared() > 0.1f)
          {
            Vector3 swingDir = tangentVelocity.Normalized();
            float centrifugalPull = Mathf.Abs(tensionMag) * 0.002f;
            Vector3 slingshotForce = swingDir * centrifugalPull * SlingshotBoostMultiplier;

            if (isReelingNow) slingshotForce += swingDir * ReelWhipForce;

            totalForce += slingshotForce;
          }

          float distributedRatio = 0.85f;
          float focalRatio = 0.15f;

          foreach (var m in _muscles)
          {
            Vector3 distForce = (totalForce * distributedRatio) * (m.Bone.Mass / totalMass);
            ApplyForce(m.Bone, distForce, dt);
          }

          ApplyForce(hand, totalForce * focalRatio, dt);
        }
      }
    }
  }

  // ------------------------------------------------------------------
  // POSE MATCHING & STABILIZATION
  // ------------------------------------------------------------------
  private void ApplyPoseMatching(float dt, bool logDebug, float strengthMult, HashSet<int> activeStanceBones)
  {
    bool isAnyGrappling = _isGrapplingL || _isGrapplingR;

    for (int i = 0; i < _muscles.Count; i++)
    {
      var m = _muscles[i];
      if (m.Bone == _hips && !_wasFallen && !isAnyGrappling && !FlightModeActive) continue;

      float currentStrengthMult = strengthMult;
      Quaternion targetQ;

      if (isAnyGrappling)
      {
        if (m.IsLeftArm && _isGrapplingL && _grapplePathL.Count > 0)
        {
          Vector3 currentAnchor = _grapplePathL[_grapplePathL.Count - 1];
          Vector3 forward = (currentAnchor - m.Bone.GlobalPosition).Normalized();
          Vector3 right = Vector3.Up.Cross(forward).Normalized();
          if (right.LengthSquared() < 0.001f) right = Vector3.Right;
          Vector3 up = forward.Cross(right).Normalized();
          Basis lookBasis = new Basis(right, up, forward);
          targetQ = lookBasis.GetRotationQuaternion();
          currentStrengthMult = 8.0f;
        }
        else if (m.IsRightArm && _isGrapplingR && _grapplePathR.Count > 0)
        {
          Vector3 currentAnchor = _grapplePathR[_grapplePathR.Count - 1];
          Vector3 forward = (currentAnchor - m.Bone.GlobalPosition).Normalized();
          Vector3 right = Vector3.Up.Cross(forward).Normalized();
          if (right.LengthSquared() < 0.001f) right = Vector3.Right;
          Vector3 up = forward.Cross(right).Normalized();
          Basis lookBasis = new Basis(right, up, forward);
          targetQ = lookBasis.GetRotationQuaternion();
          currentStrengthMult = 8.0f;
        }
        else if (m.IsArm)
        {
          currentStrengthMult = 0.05f;
          Transform3D targetLocal = AnimationShadow.GetBoneGlobalPose(m.BoneId);
          targetQ = (AnimationShadow.GlobalTransform * targetLocal).Basis.GetRotationQuaternion();
        }
        else if (m.IsLeg)
        {
          currentStrengthMult = 0.02f;
          Transform3D targetLocal = AnimationShadow.GetBoneGlobalPose(m.BoneId);
          targetQ = (AnimationShadow.GlobalTransform * targetLocal).Basis.GetRotationQuaternion();
        }
        else
        {
          Transform3D targetLocal = AnimationShadow.GetBoneGlobalPose(m.BoneId);
          targetQ = (AnimationShadow.GlobalTransform * targetLocal).Basis.GetRotationQuaternion();
          if (m.IsSpine) currentStrengthMult = 0.4f;
        }
      }
      else
      {
        Transform3D targetLocal = AnimationShadow.GetBoneGlobalPose(m.BoneId);
        Transform3D targetWorld = AnimationShadow.GlobalTransform * targetLocal;
        targetQ = targetWorld.Basis.GetRotationQuaternion();
      }

      Quaternion currentQ = m.Bone.GlobalBasis.GetRotationQuaternion();
      Quaternion diff = (targetQ * currentQ.Inverse()).Normalized();
      if (diff.W < 0f) diff = new Quaternion(-diff.X, -diff.Y, -diff.Z, -diff.W);

      Vector3 qVec = new Vector3(diff.X, diff.Y, diff.Z);
      float sinHalfAngle = qVec.Length();
      if (sinHalfAngle < 0.001f) continue;

      Vector3 axis = qVec / sinHalfAngle;
      float angle = 2.0f * Mathf.Atan2(sinHalfAngle, diff.W);

      float stiffness = MuscleStiffness * currentStrengthMult;
      float damping = MuscleDamping;

      if (angle > ImpactRelaxationAngle && !FlightModeActive)
      {
        stiffness *= 0.05f;
        damping *= ImpactDampingSpike;
      }

      if (activeStanceBones.Contains(m.BoneId)) {
        float kneeExtension = Vector3.Up.Dot(m.Bone.GlobalBasis.Y);
        stiffness *= (m.IsLeg && kneeExtension > 0.9f) ? 0.3f : 0.05f;
      }

      if (m.IsSpine) { stiffness *= 10.0f; damping *= 2.0f; }
      else if (m.IsHead)
      {
        if (FlightModeActive)
        {
          stiffness = FlightHeadStiffnessOverride;
          damping = FlightHeadDampingOverride;
          Transform3D targetLocal = AnimationShadow.GetBoneGlobalPose(m.BoneId);
          Basis biasedBasis = (AnimationShadow.GlobalTransform * targetLocal).Basis * Basis.FromEuler(new Vector3(Mathf.DegToRad(FlightHeadPitchGazeBias), 0, 0));
          targetQ = biasedBasis.GetRotationQuaternion();
          diff = (targetQ * currentQ.Inverse()).Normalized();
          if (diff.W < 0f) diff = new Quaternion(-diff.X, -diff.Y, -diff.Z, -diff.W);
          qVec = new Vector3(diff.X, diff.Y, diff.Z);
          sinHalfAngle = qVec.Length();
          if (sinHalfAngle > 0.001f) {
            axis = qVec / sinHalfAngle;
            angle = 2.0f * Mathf.Atan2(sinHalfAngle, diff.W);
          }
        }
        else stiffness *= 2.0f;
      }
      else if (m.IsArm && !isAnyGrappling)
      {
        if (FlightModeActive) { stiffness = 2000.0f; damping = 30.0f; }
        else { stiffness *= 0.3f; damping *= 0.5f; }
      }

      Vector3 pTerm = (axis * angle) * stiffness;
      Vector3 currentAngVel = m.Bone.AngularVelocity;
      Vector3 dampingRef = (m.ParentBone != null) ? m.ParentBone.AngularVelocity : (currentAngVel * 0.1f);
      Vector3 relativeVel = currentAngVel - dampingRef;
      Vector3 dTerm = relativeVel * damping;

      if (m.IsSpine) {
        Vector3 localAngVel = m.Bone.GlobalBasis.Inverse() * relativeVel;
        Vector3 twistDamp = m.Bone.GlobalBasis * (new Vector3(0, localAngVel.Y, 0) * 5000.0f);
        dTerm += twistDamp;
      }

      Vector3 rawTorque = pTerm - dTerm;
      float limit = MaxMuscleTorque;
      if (m.IsSpine) limit *= 5.0f;
      if (m.IsArm && !isAnyGrappling) limit *= 0.2f;
      else if ((m.IsLeftArm && _isGrapplingL) || (m.IsRightArm && _isGrapplingR)) limit *= 8.0f;

      if (m.IsSpine && !isAnyGrappling)
      {
        Vector3 iTerm = m.IntegralError * (stiffness * 0.15f);
        Vector3 proposedTorque = rawTorque + iTerm;
        if (proposedTorque.LengthSquared() < (limit * limit)) {
          if (angle < 0.4f) m.IntegralError += (axis * angle) * dt;
        }
        float iMax = 1.2f;
        if (m.IntegralError.LengthSquared() > iMax * iMax) m.IntegralError = m.IntegralError.Normalized() * iMax;
        rawTorque = proposedTorque;
      }

      ApplyTorque(m.Bone, rawTorque.LimitLength(limit), dt, m.ParentBone);
    }
  }

  private void ApplyGravityCompensation(float dt)
  {
    for(int i = 0; i < _muscles.Count; i++)
    {
      var m = _muscles[i];
      if (m.IsFinger || m.SubtreeMass <= 0.001f) continue;
      Vector3 pivot = m.Bone.GlobalPosition;
      Vector3 compositeCOM = m.Bone.GlobalTransform * m.InitialLocalSubtreeCOM;
      Vector3 leverArm = compositeCOM - pivot;
      Vector3 gravityForce = Vector3.Down * 9.81f * m.SubtreeMass;
      Vector3 counterTorque = leverArm.Cross(gravityForce);
      float effectiveComp = FlightModeActive && m.IsHead ? GravityComp * 1.4f : GravityComp;
      ApplyTorque(m.Bone, -counterTorque * effectiveComp, dt, m.ParentBone);
    }
  }

  private void ApplyAerodynamicDrag(float dt)
  {
    for (int i = 0; i < _muscles.Count; i++)
    {
      var m = _muscles[i];
      Vector3 relativeVelocity = m.Bone.LinearVelocity - _ambientWind;
      float speedSq = relativeVelocity.LengthSquared();
      if (speedSq > 0.01f)
      {
        Vector3 velDir = relativeVelocity.Normalized();
        float longitudinalExposure = Mathf.Abs(velDir.Dot(m.Bone.GlobalBasis.Y));
        float lateralExposure = 1.0f - longitudinalExposure;
        float effectiveArea = m.Bone.Mass * (0.02f * longitudinalExposure + 0.15f * lateralExposure);
        float currentShear = FlightModeActive ? 0.3f : WindShearMultiplier;
        float dragMagnitude = 0.5f * AirDensity * speedSq * BaseDragCoefficient * effectiveArea;
        Vector3 dragForce = -velDir * dragMagnitude * currentShear;
        ApplyForce(m.Bone, dragForce, dt);
      }
    }
  }

  private void ApplyCoreStabilization(ref RagdollState state, float dt)
  {
    if (_hips == null) return;
    Vector3 avgFootPos = Vector3.Zero;
    for (int i = 0; i < _groundedLegsBuffer.Count; i++) avgFootPos += _groundedLegsBuffer[i].Foot.GlobalPosition;
    Vector3 targetUp = Vector3.Up;
    if (_groundedLegsBuffer.Count > 0) {
      avgFootPos /= _groundedLegsBuffer.Count;
      Vector3 balanceError = state.CenterOfMass - avgFootPos;
      Vector3 hipForward = _hips.GlobalBasis.Z;
      float forwardLeanError = balanceError.Dot(hipForward);
      targetUp = (Vector3.Up - (hipForward * forwardLeanError)).Normalized();
    }
    Vector3 currentUp = _hips.GlobalBasis.Y;
    Vector3 rotAxis = currentUp.Cross(targetUp);
    float rotAngle = Mathf.Acos(Mathf.Clamp(currentUp.Dot(targetUp), -1f, 1f));
    if (rotAngle > 0.001f)
    {
      rotAxis = rotAxis.Normalized();
      ApplyTorque(_hips, rotAxis * rotAngle * HipGyroStiffness - _hips.AngularVelocity * HipGyroDamping, dt);
    }
  }

  private void ApplySpinalExtension(float dt)
  {
    if (_chest == null || _hips == null) return;
    Vector3 force = Vector3.Up * 3000.0f;
    ApplyForce(_chest, force, dt);
    ApplyForce(_hips, -force, dt);
  }

  private void ApplyVirtualModelControl(ref RagdollState state, float dt)
  {
    if (_hips == null) return;
    Vector3 hipVel = _hips.LinearVelocity;
    float currentHeight = _hips.GlobalPosition.Y;
    float heightError = TargetHeight - currentHeight;
    float verticalForceMag = (heightError * SupportSpring) - (hipVel.Y * SupportDamp);
    verticalForceMag = Mathf.Clamp(verticalForceMag, 0, MaxForce);

    if (state.IsFallen || _groundedLegsBuffer.Count == 0) {
      if (state.IsFallen) ApplyForce(_hips, Vector3.Up * verticalForceMag, dt);
      return;
    }

    Vector3 avgFootPos = Vector3.Zero;
    for (int i = 0; i < _groundedLegsBuffer.Count; i++) {
      Vector3 footForward = -_groundedLegsBuffer[i].Foot.GlobalBasis.Z;
      footForward.Y = 0;
      if (footForward.LengthSquared() > 0.001f) footForward = footForward.Normalized();
      avgFootPos += _groundedLegsBuffer[i].Foot.GlobalPosition + (footForward * CenterOfPressureOffset);
    }
    avgFootPos /= _groundedLegsBuffer.Count;

    Vector3 horizontalError = avgFootPos - state.CenterOfMass;
    horizontalError.Y = 0;
    Vector3 horizontalForce = (horizontalError * BalanceStiffness) - (new Vector3(hipVel.X, 0, hipVel.Z) * BalanceDamping);
    Vector3 desiredBodyForce = (Vector3.Up * verticalForceMag) + horizontalForce;
    if (desiredBodyForce.LengthSquared() > MaxForce * MaxForce) desiredBodyForce = desiredBodyForce.Normalized() * MaxForce;
    Vector3 footReactionForce = -desiredBodyForce;

    float[] legWeights = new float[_groundedLegsBuffer.Count];
    float totalWeight = 0f;

    for (int i = 0; i < _groundedLegsBuffer.Count; i++)
    {
      float dist = _groundedLegsBuffer[i].Foot.GlobalPosition.DistanceTo(state.CenterOfMass);
      float w = 1.0f / (dist + 0.01f);
      legWeights[i] = w;
      totalWeight += w;
    }

    for (int i = 0; i < _groundedLegsBuffer.Count; i++)
    {
      float share = legWeights[i] / totalWeight;
      Vector3 legForce = footReactionForce * share;
      LimbChain leg = _groundedLegsBuffer[i];

      Vector3 footForward = -leg.Foot.GlobalBasis.Z;
      footForward.Y = 0;
      if (footForward.LengthSquared() > 0.001f) footForward = footForward.Normalized();
      Vector3 midFootPos = leg.Foot.GlobalPosition + (footForward * CenterOfPressureOffset);

      Vector3 r_Hip = midFootPos - leg.UpperLeg.GlobalPosition;
      Vector3 r_Knee = midFootPos - leg.LowerLeg.GlobalPosition;
      Vector3 r_Ankle = midFootPos - leg.Foot.GlobalPosition;

      Vector3 hipTorque = r_Hip.Cross(legForce).LimitLength(MaxMuscleTorque);
      Vector3 kneeTorque = r_Knee.Cross(legForce).LimitLength(MaxMuscleTorque);
      Vector3 ankleTorque = r_Ankle.Cross(legForce).LimitLength(MaxMuscleTorque);

      ApplyTorque(leg.Foot, ankleTorque, dt, leg.LowerLeg);
      ApplyTorque(leg.LowerLeg, kneeTorque, dt, leg.UpperLeg);
      ApplyTorque(leg.UpperLeg, hipTorque, dt, _hips);
    }
  }

  private void ApplyHipRecovery(float dt)
  {
    if (_hipMuscle == null || _hips == null) return;
    Transform3D targetTrans = AnimationShadow.GetBoneGlobalPose(_hipMuscle.BoneId);
    Quaternion targetQ = (AnimationShadow.GlobalTransform * targetTrans).Basis.GetRotationQuaternion();
    Quaternion currentQ = _hips.GlobalBasis.GetRotationQuaternion();
    Quaternion diff = (targetQ * currentQ.Inverse()).Normalized();
    if (diff.W < 0f) diff = new Quaternion(-diff.X, -diff.Y, -diff.Z, -diff.W);

    Vector3 qVec = new Vector3(diff.X, diff.Y, diff.Z);
    float sinHalfAngle = qVec.Length();
    if (sinHalfAngle < 0.001f) return;

    Vector3 axis = qVec / sinHalfAngle;
    float angle = 2.0f * Mathf.Atan2(sinHalfAngle, diff.W);

    if (angle > 0.01f) {
      Vector3 pTerm = (axis * angle) * RecoveryStiffness;
      Vector3 dTerm = _hips.AngularVelocity * RecoveryDamping;
      ApplyTorque(_hips, pTerm - dTerm, dt);
    }
  }

  private void ApplyAnkleStrategy(float dt)
  {
    for (int i = 0; i < _legs.Count; i++) {
      var leg = _legs[i];
      Vector3 groundNormal = Vector3.Up;
      if (leg.GroundSensor.IsColliding()) groundNormal = leg.GroundSensor.GetCollisionNormal(0);

      Vector3 footUp = leg.Foot.GlobalBasis.Y;
      Vector3 rotAxis = footUp.Cross(groundNormal);
      float rotAngle = Mathf.Acos(Mathf.Clamp(footUp.Dot(groundNormal), -1f, 1f));

      if (rotAngle > 0.01f) {
        rotAxis = rotAxis.Normalized();
        Vector3 pTerm = rotAxis * rotAngle * AnkleStiffness;
        Vector3 dTerm = leg.Foot.AngularVelocity * AnkleDamping;
        ApplyTorque(leg.Foot, pTerm - dTerm, dt, leg.LowerLeg);
      }
    }
  }

  private void ApplyTorque(PhysicalBone3D bone, Vector3 torque, float dt, PhysicalBone3D? reactionBody = null) {
    if (bone == null) return;
    PhysicsServer3D.BodyApplyTorqueImpulse(bone.GetRid(), torque * dt);
    if (reactionBody != null) PhysicsServer3D.BodyApplyTorqueImpulse(reactionBody.GetRid(), -torque * dt);
  }

  private void ApplyForce(PhysicalBone3D bone, Vector3 force, float dt) {
    if (bone == null) return;
    PhysicsServer3D.BodyApplyCentralImpulse(bone.GetRid(), force * dt);
  }

  private PhysicalBone3D? FindBonePhys(string namePart) {
    for (int i = 0; i < _muscles.Count; i++) {
      if (_muscles[i].Bone.Get("bone_name").AsString().Contains(namePart)) return _muscles[i].Bone;
    }
    return null;
  }

  private int FindBoneIndex(string boneName) => AnimationShadow.FindBone(boneName);

  private void SetupDebugGizmos() {
    if (_gizmoInstance.GetParent() == null) AddChild(_gizmoInstance);
    _gizmoInstance.Mesh = _gizmoMesh;
    _gizmoInstance.MaterialOverride = new StandardMaterial3D { ShadingMode = StandardMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true };
  }

  private void DrawGizmos(ref RagdollState state) {
    if (_hips == null) return;
    _gizmoMesh.ClearSurfaces();
    _gizmoMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);

    if (_isGrapplingL && _grappleHandL != null && _grapplePathL.Count > 0) {
      _gizmoMesh.SurfaceSetColor(Colors.Cyan);
      _gizmoMesh.SurfaceAddVertex(_grappleHandL.GlobalPosition);
      _gizmoMesh.SurfaceAddVertex(_grapplePathL[_grapplePathL.Count - 1]);
    }

    if (_isGrapplingR && _grappleHandR != null && _grapplePathR.Count > 0) {
      _gizmoMesh.SurfaceSetColor(Colors.Cyan);
      _gizmoMesh.SurfaceAddVertex(_grappleHandR.GlobalPosition);
      _gizmoMesh.SurfaceAddVertex(_grapplePathR[_grapplePathR.Count - 1]);
    }

    _gizmoMesh.SurfaceSetColor(Colors.Magenta);
    _gizmoMesh.SurfaceAddVertex(state.CenterOfMass);
    _gizmoMesh.SurfaceAddVertex(state.CenterOfMass + Vector3.Down * TargetHeight);

    Vector3 predictedCoM = state.CenterOfMass + (_hips.LinearVelocity * 0.2f);
    _gizmoMesh.SurfaceSetColor(Colors.Yellow);
    _gizmoMesh.SurfaceAddVertex(state.CenterOfMass);
    _gizmoMesh.SurfaceAddVertex(predictedCoM);

    for (int i = 0; i < _legs.Count; i++) {
      var leg = _legs[i];
      Vector3 start = leg.Foot.GlobalPosition;
      Vector3 end = leg.GroundSensor.IsColliding() ? leg.GroundSensor.GetCollisionPoint(0) : start + (Vector3.Down * 0.5f);
      _gizmoMesh.SurfaceSetColor(leg.GroundSensor.IsColliding() ? Colors.Green : Colors.Red);
      _gizmoMesh.SurfaceAddVertex(start);
      _gizmoMesh.SurfaceAddVertex(end);
    }
    _gizmoMesh.SurfaceEnd();
  }
}
