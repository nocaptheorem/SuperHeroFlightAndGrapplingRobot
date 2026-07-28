using Godot;

public partial class Main : Node3D
{
  private const string BotScenePath = "res://x_bot.tscn";

  // No camera needed – the ragdoll provides its own FPS camera.
  private Node3D _botInstance = null!;

  public override void _Ready()
  {
    BuildLighting();
    SpawnBot();
  }


  private void BuildLighting()
  {
    var sun = new DirectionalLight3D();
    sun.ShadowEnabled = true;
    sun.Position = new Vector3(5, 10, 5);
    AddChild(sun);
    sun.LookAt(Vector3.Zero);

    var env = new WorldEnvironment();
    env.Environment = new Godot.Environment
    {
      BackgroundMode = Godot.Environment.BGMode.Sky,
      Sky = new Sky { SkyMaterial = new ProceduralSkyMaterial() },
      AmbientLightSource = Godot.Environment.AmbientSource.Sky,
      TonemapMode = Godot.Environment.ToneMapper.Filmic
    };
    AddChild(env);
  }

  private void SpawnBot()
  {
    if (!ResourceLoader.Exists(BotScenePath))
    {
      GD.PrintErr($"CRASH: Could not find scene at {BotScenePath}");
      return;
    }

    var scene = GD.Load<PackedScene>(BotScenePath);
    _botInstance = scene.Instantiate<Node3D>();
    AddChild(_botInstance);
    _botInstance.GlobalPosition = new Vector3(0, 100.0f, 0); // Spawns the bot high in the air
  }
}
